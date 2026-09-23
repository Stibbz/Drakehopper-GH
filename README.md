# Grasshopper Custom Components

Custom Grasshopper components for Rhino 8, written in C#.

| Component | Folder | What it does |
|---|---|---|
| Auto Internalise | [`AutoInternalise/`](AutoInternalise) | Keeps a definition runnable on its own by internalizing data referenced from Rhino and Rhino.Inside.Revit. |

## Auto Internalise

Found under **Params → Util → Auto Internalise**.

A Grasshopper definition that references geometry in a Rhino file, or reads elements from Revit through
Rhino.Inside.Revit, stops working when that file or model isn't there. This component internalizes that
data into the `.gh` file so the definition keeps running standalone.

### Inputs

All inputs default to `False`.

| Input | Mode | Behaviour |
|---|---|---|
| **Rhino** | Continuous | Internalizes geometry referenced from the Rhino file ("Set one curve", etc.): the geometry is stored in the `.gh` file and the link to the Rhino object is dropped. |
| **Revit** | Continuous | Internalizes the data coming *out of* Rhino.Inside.Revit components (e.g. the Geometry output of Element Geometry) into the first regular Grasshopper parameter it flows into, and removes that wire. Everything downstream then runs without Revit. |
| **All (once)** | One-off | Each time it switches to `True`: internalizes every parameter that supports it, including all wired inputs (wires are removed). |

**Continuous** means:

- Switching the toggle on runs immediately.
- After that it runs at most once every **15 minutes**. Rhino and Revit share this cooldown and run together.
- It always runs when the file is **saved**, ignoring the cooldown, and then saves the file again so the file on disk is standalone.

The line under the node shows the state, e.g. `Auto Rhino+Revit / 15 min · next 14:35`.

### Outputs

| Output | Contents |
|---|---|
| **Internalized Nodes** | Nodes this component internalized since a toggle was last switched on. |
| **All Internalized Nodes** | Every node in the file that holds internalized data, whether internalized manually or by this component. Default values of component inputs and still-linked Rhino references are not counted. |
| **Details** | Per item: node, Rhino object ID, type, name and layer, or the number of items taken from wires. |
| **Failed** | What couldn't be internalized and why (see below). |

Internalized nodes are outlined in orange on the canvas until a toggle is switched on again. Every run is a
single undo step.

### Things to know

- **Internalizing freezes the link.** Once a wire from Revit is removed, later changes in the Revit model no
  longer reach that part of the definition. To pick up changes: switch Revit off, reconnect, let it
  recompute, switch Revit on again.
- **Revit must deliver data at capture time.** If Revit isn't running, the model is closed or the Revit
  component has an error, nothing is overwritten; the parameter is listed under Failed and retried later.
- **Revit elements themselves can't be stored standalone.** If the data is still Revit elements (e.g. fed
  into a generic Data parameter), feed it through Element Geometry or another component that outputs Rhino
  data first.
- **Rhino references need the Rhino file open once.** References to objects that aren't in the open Rhino
  file can't be loaded; they're listed under Failed.
- **Not covered:** components that read the Rhino model at runtime instead of holding a reference, such as
  Geometry Pipeline, the Rhino 8 model components (Query Model Objects, Model Object, …) and scripts that
  read the Rhino document.

## Building

Requirements: Windows, Rhino 8 installed in `C:\Program Files\Rhino 8`, and the .NET SDK.

```powershell
dotnet build AutoInternalise -c Release
```

The project references `RhinoCommon.dll`, `Grasshopper.dll` and `GH_IO.dll` from the Rhino install. If Rhino
is installed elsewhere, pass the path: `dotnet build AutoInternalise -c Release -p:RhinoDir="D:\Rhino 8"`.

## Installing

Copy the built plugin to Grasshopper's user library folder (close Rhino first; a loaded `.gha` is locked):

```powershell
Copy-Item AutoInternalise\bin\Release\AutoInternalise.gha "$env:APPDATA\Grasshopper\Libraries\" -Force
```

Restart Rhino and Grasshopper.
