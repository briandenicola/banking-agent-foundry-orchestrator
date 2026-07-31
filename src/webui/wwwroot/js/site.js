const requestInput = document.querySelector("#Input_UserMessage");
const demoScenarioInput = document.querySelector("#Input_DemoScenario");
const scenarioButtons = document.querySelectorAll("[data-scenario]");

scenarioButtons.forEach((button) => {
  button.addEventListener("click", () => {
    if (!requestInput || !demoScenarioInput) return;

    requestInput.value = button.dataset.prompt ?? "";
    demoScenarioInput.value = button.dataset.scenario ?? "";
    scenarioButtons.forEach((candidate) => {
      candidate.classList.toggle("is-selected", candidate === button);
      candidate.setAttribute("aria-pressed", candidate === button ? "true" : "false");
    });
    requestInput.focus();
  });
});

requestInput?.addEventListener("input", () => {
  if (!demoScenarioInput) return;

  demoScenarioInput.value = "";
  scenarioButtons.forEach((button) => {
    button.classList.remove("is-selected");
    button.setAttribute("aria-pressed", "false");
  });
});

document.querySelectorAll("[data-loading-form]").forEach((form) => {
  form.addEventListener("submit", () => {
    if (form.checkValidity()) {
      form.classList.add("is-loading");
    }
  });
});

const evidenceInput = document.querySelector("#Input_EvidenceFiles");
const selectedFiles = document.querySelector("[data-selected-files]");
if (evidenceInput && selectedFiles) {
  evidenceInput.addEventListener("change", () => {
    selectedFiles.replaceChildren();
    Array.from(evidenceInput.files ?? []).forEach((file) => {
      const item = document.createElement("span");
      item.textContent = `${file.name} · ${(file.size / 1024 / 1024).toFixed(1)} MB`;
      selectedFiles.appendChild(item);
    });
  });
}
