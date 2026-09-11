"""Static tests for the cloud log reader.

These never touch Azure. They cover the parts that can be wrong silently:
lookback validation, KQL escaping, the short-name to deployed-name mapping,
and the rule that a section which found nothing still reports itself.
"""
from __future__ import annotations

import argparse
import importlib.util
import io
import json
import sys
from contextlib import redirect_stdout
from pathlib import Path

import pytest

SCRIPTS = Path(__file__).resolve().parents[1]


def _load(name: str, filename: str):
    spec = importlib.util.spec_from_file_location(name, SCRIPTS / filename)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


azure_logs = _load("azure_logs", "azure_logs.py")
cloud_logs = _load("cloud_logs", "cloud-logs.py")


class TestParseSince:
    @pytest.mark.parametrize("value", ["30m", "2h", "7d", "1m", "180d"])
    def test_accepts_supported_units(self, value):
        assert cloud_logs.parse_since(value) == value

    @pytest.mark.parametrize("value", ["", "30", "h", "2w", "2 h", "-1h", "1.5h", "30s", "2h; drop"])
    def test_rejects_everything_else(self, value):
        # The value is interpolated into KQL, so anything unrecognised must be
        # refused here rather than sent to the service.
        with pytest.raises(argparse.ArgumentTypeError):
            cloud_logs.parse_since(value)


class TestKqlString:
    def test_escapes_single_quotes(self):
        assert cloud_logs.kql_string("o'brien") == "o\\'brien"

    def test_escapes_backslashes_before_quotes(self):
        assert cloud_logs.kql_string("a\\b'c") == "a\\\\b\\'c"

    def test_leaves_ordinary_text_alone(self):
        assert cloud_logs.kql_string("whippet-59851-orchestrator") == "whippet-59851-orchestrator"


class TestFailureRate:
    def test_small_rate_does_not_round_to_zero(self):
        # Three failures in 605 calls is 0.5%. Reporting 0 next to a non-zero
        # count invites the reader to treat it as none.
        assert cloud_logs.failure_rate(3, 605) == 0.5

    def test_no_calls_yields_none_rather_than_zero(self):
        assert cloud_logs.failure_rate(0, 0) is None

    def test_clean_run_is_zero(self):
        assert cloud_logs.failure_rate(0, 100) == 0.0


class TestAsInt:
    @pytest.mark.parametrize(
        "value,expected", [("5", 5), (5, 5), (5.9, 5), ("5.0", 5), (None, 0), ("", 0), ("x", 0)]
    )
    def test_coerces_or_defaults(self, value, expected):
        assert cloud_logs.as_int(value) == expected


class TestResolveContainerApp:
    def test_maps_short_name_onto_deployed_name(self, monkeypatch):
        monkeypatch.setattr(
            azure_logs,
            "list_container_apps",
            lambda rg: ["whippet-59851-orchestrator", "whippet-59851-webui"],
        )
        assert (
            azure_logs.resolve_container_app("rg", "orchestrator")
            == "whippet-59851-orchestrator"
        )

    def test_accepts_a_full_name_unchanged(self, monkeypatch):
        monkeypatch.setattr(
            azure_logs, "list_container_apps", lambda rg: ["whippet-59851-webui"]
        )
        assert azure_logs.resolve_container_app("rg", "whippet-59851-webui") == "whippet-59851-webui"

    def test_unknown_name_lists_the_real_ones(self, monkeypatch):
        monkeypatch.setattr(
            azure_logs,
            "list_container_apps",
            lambda rg: ["whippet-59851-orchestrator", "whippet-59851-webui"],
        )
        with pytest.raises(azure_logs.AzureQueryError) as error:
            azure_logs.resolve_container_app("rg", "nope")
        # A bare "not found" leaves the operator guessing at the spelling.
        assert "whippet-59851-orchestrator" in str(error.value)

    def test_ambiguous_suffix_refuses_to_guess(self, monkeypatch):
        monkeypatch.setattr(
            azure_logs, "list_container_apps", lambda rg: ["a-webui", "b-webui"]
        )
        with pytest.raises(azure_logs.AzureQueryError):
            azure_logs.resolve_container_app("rg", "webui")

    def test_empty_resource_group_says_so(self, monkeypatch):
        monkeypatch.setattr(azure_logs, "list_container_apps", lambda rg: [])
        with pytest.raises(azure_logs.AzureQueryError):
            azure_logs.resolve_container_app("rg", "orchestrator")


def _run_model(monkeypatch, responses):
    """Drive command_model with canned query results, capturing its JSON."""
    calls = iter(responses)
    monkeypatch.setattr(cloud_logs, "resolve_container_app", lambda rg, app: "app")
    monkeypatch.setattr(cloud_logs, "resolve_workspace_id", lambda rg, app: "ws")
    monkeypatch.setattr(cloud_logs, "run_kql", lambda ws, query: next(calls))
    args = argparse.Namespace(since="7d", limit=10, app="orchestrator")
    buffer = io.StringIO()
    with redirect_stdout(buffer):
        cloud_logs.command_model(args, "rg")
    return json.loads(buffer.getvalue())


class TestModelReportReportsEmptiness:
    def test_no_throttling_still_reports_the_key_and_the_denominator(self, monkeypatch):
        # A report that omits rate_limited when there are no 429s is
        # indistinguishable from one whose query broke. The count must be
        # present and zero, and requests_seen proves diagnostics are arriving.
        document = _run_model(
            monkeypatch,
            [
                [],
                [{"ResultSignature": "200", "OperationName": "responses", "requests": 12}],
                [],
                [],
            ],
        )
        assert document["rate_limited"]["count"] == 0
        assert document["rate_limited"]["requests_seen"] == 12
        assert document["rate_limited"]["diagnostics_delivering"] is True

    def test_silent_diagnostics_are_distinguishable_from_a_clean_run(self, monkeypatch):
        document = _run_model(monkeypatch, [[], [], [], []])
        assert document["rate_limited"]["count"] == 0
        assert document["rate_limited"]["diagnostics_delivering"] is False

    def test_every_section_is_present_even_when_all_are_empty(self, monkeypatch):
        document = _run_model(monkeypatch, [[], [], [], []])
        for section in (
            "agent_invocations",
            "rate_limited",
            "edge_failures",
            "model_usage",
            "exceptions",
        ):
            assert document[section]["count"] == 0
            assert document[section]["source"]

    def test_throttling_is_counted_and_kept_out_of_edge_failures(self, monkeypatch):
        document = _run_model(
            monkeypatch,
            [
                [],
                [
                    {"ResultSignature": "429", "OperationName": "responses", "requests": 7},
                    {"ResultSignature": "404", "OperationName": "Projects_Get", "requests": 2},
                    {"ResultSignature": "200", "OperationName": "responses", "requests": 90},
                ],
                [],
                [],
            ],
        )
        assert document["rate_limited"]["count"] == 7
        # 429 belongs to rate_limited only; double-counting it here would
        # overstate the failure total.
        assert document["edge_failures"]["count"] == 2

    def test_agent_name_and_phase_come_from_the_invocation_path(self, monkeypatch):
        document = _run_model(
            monkeypatch,
            [
                [
                    {
                        "Name": "POST /api/projects/p/agents/workflow-planning/endpoint/protocols/invocations",
                        "calls": 605,
                        "failures": 3,
                        "p95_ms": 6212.4,
                    }
                ],
                [],
                [],
                [],
            ],
        )
        agent = document["agent_invocations"]["by_agent"][0]
        assert agent["agent"] == "workflow-planning"
        assert agent["phase"] == "planner"
        assert agent["failure_rate_percent"] == 0.5
        assert agent["p95_ms"] == 6212

    def test_layers_are_labelled_so_they_are_not_summed(self, monkeypatch):
        document = _run_model(monkeypatch, [[], [], [], []])
        assert "AppDependencies" in document["agent_invocations"]["source"]
        assert "AzureDiagnostics" in document["rate_limited"]["source"]


class TestRecentLogs:
    def _run(self, monkeypatch, rows, **overrides):
        monkeypatch.setattr(cloud_logs, "resolve_container_app", lambda rg, app: "deployed-app")
        monkeypatch.setattr(cloud_logs, "resolve_workspace_id", lambda rg, app: "ws")
        captured = {}

        def fake_kql(workspace, query):
            captured["query"] = query
            return list(rows)

        monkeypatch.setattr(cloud_logs, "run_kql", fake_kql)
        args = argparse.Namespace(app="orchestrator", since="2h", limit=2, grep=None)
        for key, value in overrides.items():
            setattr(args, key, value)
        buffer = io.StringIO()
        with redirect_stdout(buffer):
            cloud_logs.command_recent(args, "rg")
        return json.loads(buffer.getvalue()), captured["query"]

    def test_emits_pipeable_json_with_an_explicit_count(self, monkeypatch):
        document, _ = self._run(monkeypatch, [])
        assert document["count"] == 0
        assert document["entries"] == []
        assert document["app"] == "deployed-app"

    def test_orders_oldest_first_for_reading(self, monkeypatch):
        # The query takes the newest N, then the output is reversed so a tail
        # reads in chronological order.
        document, _ = self._run(
            monkeypatch,
            [
                {"TimeGenerated": "2026-01-01T00:00:02Z", "RevisionName_s": "app--rev", "Log_s": "second"},
                {"TimeGenerated": "2026-01-01T00:00:01Z", "RevisionName_s": "app--rev", "Log_s": "first"},
            ],
        )
        assert [entry["log"] for entry in document["entries"]] == ["first", "second"]

    def test_hitting_the_limit_is_flagged(self, monkeypatch):
        rows = [{"TimeGenerated": "t", "RevisionName_s": "app--rev", "Log_s": "x"}] * 2
        document, _ = self._run(monkeypatch, rows)
        # Without this, a truncated tail looks like the whole story.
        assert document["truncated"] is True

    def test_revision_is_shortened_but_the_full_name_is_kept(self, monkeypatch):
        document, _ = self._run(
            monkeypatch,
            [{"TimeGenerated": "t", "RevisionName_s": "whippet-59851-orchestrator--0000002", "Log_s": "x"}],
        )
        assert document["entries"][0]["revision"] == "0000002"
        assert document["entries"][0]["revision_full"] == "whippet-59851-orchestrator--0000002"

    def test_log_text_is_collapsed_onto_one_line(self, monkeypatch):
        document, _ = self._run(
            monkeypatch,
            [{"TimeGenerated": "t", "RevisionName_s": "a--b", "Log_s": "a\n  b\tc  "}],
        )
        assert document["entries"][0]["log"] == "a b c"

    def test_grep_is_escaped_into_the_query(self, monkeypatch):
        _, query = self._run(monkeypatch, [], grep="o'brien")
        assert "o\\'brien" in query
        assert "matches regex" in query

    def test_no_grep_adds_no_filter(self, monkeypatch):
        _, query = self._run(monkeypatch, [])
        assert "matches regex" not in query


class TestParser:
    def test_defaults_to_the_orchestrator(self):
        args = cloud_logs.build_parser().parse_args([])
        assert args.app == "orchestrator"
        assert args.model is False
        assert args.follow is False

    def test_a_later_since_overrides_an_earlier_one(self):
        # cloud:logs:model supplies --since 24h, and a user's -- --since 7d
        # arrives after it. argparse must let the user's value win.
        args = cloud_logs.build_parser().parse_args(["--since", "24h", "--since", "7d"])
        assert args.since == "7d"

    def test_rejects_a_bad_lookback_at_the_boundary(self):
        with pytest.raises(SystemExit):
            cloud_logs.build_parser().parse_args(["--since", "2w"])


class TestReadTail:
    def _stub(self, monkeypatch, stdout, returncode=0, stderr=""):
        import subprocess as sp

        monkeypatch.setattr(
            cloud_logs.subprocess,
            "run",
            lambda *a, **k: sp.CompletedProcess(a[0], returncode, stdout, stderr),
        )

    def test_drops_the_connection_preamble(self, monkeypatch):
        # az prints two status lines per connection. Polling re-reads them
        # every interval, so they must never reach the output.
        self._stub(
            monkeypatch,
            "\n".join(
                [
                    '{"TimeStamp": "t0", "Log": "Connecting to the container \'orchestrator\'..."}',
                    '{"TimeStamp": "t1", "Log": "Successfully Connected to container: \'orchestrator\'"}',
                    '{"TimeStamp": "t2", "Log": "F real line"}',
                ]
            ),
        )
        entries = cloud_logs.read_tail("app", "rg", 10)
        assert [e["Log"] for e in entries] == ["F real line"]

    def test_skips_unparseable_lines_without_failing(self, monkeypatch):
        self._stub(monkeypatch, 'not json\n{"TimeStamp": "t", "Log": "kept"}\n\n')
        assert [e["Log"] for e in cloud_logs.read_tail("app", "rg", 10)] == ["kept"]

    def test_a_failed_command_raises_rather_than_looking_idle(self, monkeypatch):
        # Returning [] here would render a broken command as a quiet app.
        self._stub(monkeypatch, "", returncode=1, stderr="ResourceNotFound")
        with pytest.raises(azure_logs.AzureQueryError) as error:
            cloud_logs.read_tail("app", "rg", 10)
        assert "ResourceNotFound" in str(error.value)


class TestFollow:
    def _follow(self, monkeypatch, polls):
        monkeypatch.setattr(cloud_logs, "resolve_container_app", lambda rg, app: "app")
        batches = iter(polls)

        def fake_read_tail(app, rg, tail):
            try:
                return next(batches)
            except StopIteration:
                raise KeyboardInterrupt

        monkeypatch.setattr(cloud_logs, "read_tail", fake_read_tail)
        monkeypatch.setattr(cloud_logs.time, "sleep", lambda seconds: None)
        args = argparse.Namespace(app="orchestrator", limit=10, interval=0.0)
        buffer = io.StringIO()
        with redirect_stdout(buffer):
            cloud_logs.command_follow(args, "rg")
        return [json.loads(line) for line in buffer.getvalue().splitlines() if line.strip()]

    def test_emits_json_lines_not_one_document(self, monkeypatch):
        # A follower cannot close a single document, so each line stands alone.
        lines = self._follow(monkeypatch, [[{"TimeStamp": "t1", "Log": "F a"}]])
        assert lines == [
            {"timestamp": "t1", "app": "app", "log": "a", "backfill": True}
        ]

    def test_a_line_seen_again_is_not_repeated(self, monkeypatch):
        # Each poll re-reads the same tail; without dedupe every line would be
        # emitted once per interval.
        lines = self._follow(
            monkeypatch,
            [
                [{"TimeStamp": "t1", "Log": "F a"}],
                [{"TimeStamp": "t1", "Log": "F a"}, {"TimeStamp": "t2", "Log": "F b"}],
            ],
        )
        assert [line["log"] for line in lines] == ["a", "b"]

    def test_backfill_is_distinguished_from_live_lines(self, monkeypatch):
        lines = self._follow(
            monkeypatch,
            [[{"TimeStamp": "t1", "Log": "F a"}], [{"TimeStamp": "t2", "Log": "F b"}]],
        )
        assert [line["backfill"] for line in lines] == [True, False]

    def test_identical_text_at_a_new_time_is_still_emitted(self, monkeypatch):
        # Repeated health-check lines are real events, not duplicates.
        lines = self._follow(
            monkeypatch,
            [[{"TimeStamp": "t1", "Log": "F ping"}], [{"TimeStamp": "t2", "Log": "F ping"}]],
        )
        assert len(lines) == 2

    def test_stream_marker_is_stripped(self, monkeypatch):
        lines = self._follow(monkeypatch, [[{"TimeStamp": "t", "Log": "E oops"}]])
        assert lines[0]["log"] == "oops"

    def test_text_without_a_marker_is_left_intact(self, monkeypatch):
        lines = self._follow(monkeypatch, [[{"TimeStamp": "t", "Log": "plain text"}]])
        assert lines[0]["log"] == "plain text"

    def test_seen_set_stays_bounded(self, monkeypatch):
        assert cloud_logs.SEEN_LIMIT > 0
