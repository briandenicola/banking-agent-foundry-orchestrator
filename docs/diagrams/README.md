# Diagrams

## Azure architecture

[`azure-architecture.excalidraw`](azure-architecture.excalidraw) shows every Azure
resource this repository deploys, which Terraform stack owns it, and how the parts
reach each other. Open it at [excalidraw.com](https://excalidraw.com) or with the
Excalidraw VS Code extension. The icons are embedded in the file, so it renders
offline.

What the arrows mean:

| Arrow | Meaning |
| --- | --- |
| Solid blue | HTTP request flow |
| Solid green | Managed-identity access to a data or model plane |
| Dashed grey | Deployment-time only: manual jobs and image pull |

Dashed containers are optional and off by default: the Foundry memory store, the
Foundry toolbox, the Entra app registration, and private networking. The diagram
labels each one with the flag that turns it on.

## Rebuilding the diagram

The `.excalidraw` file is generated, so it can be kept in step with the Terraform
rather than drifting away from it. Edit
[`build-azure-architecture.py`](build-azure-architecture.py) and regenerate:

```bash
curl -sSL https://arch-center.azureedge.net/icons/Azure_Public_Service_Icons_V19.zip \
  -o /tmp/azicons.zip
unzip -q -o /tmp/azicons.zip -d /tmp/azicons
python3 docs/diagrams/build-azure-architecture.py
```

The icon set is Microsoft's official Azure architecture icon set. It is not
vendored here; the script downloads nothing itself and instead reads the unpacked
icons from `$AZURE_ICONS`, which defaults to
`/tmp/azicons/Azure_Public_Service_Icons/Icons`.

The generator seeds its random number generator, so regenerating without a content
change produces no diff.
