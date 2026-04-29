# C# GUI Rewrite Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Python Tkinter calibration GUI with a C# WinForms application, reducing standalone exe size from 50-150MB (PyInstaller) to ~3-5MB (framework-dependent), using ScottPlot 5's built-in draggable points for the curve editor.

**Architecture:** Single-window WinForms app with ScottPlot 5 live graph (left panel) and calibration controls (right panel). HID gamepad input via RawInput.Sharp (Windows Raw Input API wrapper). Calibration JSON serialization via System.Text.Json. Pico drive detection via Win32 GetVolumeInformation. Same calibration.json format as the Python GUI -- the firmware is unmodified.

**Tech Stack:** C# 12 / .NET 8, WinForms, ScottPlot 5 (SkiaSharp renderer), RawInput.Sharp NuGet, System.Text.Json (built-in). Build via `dotnet publish` producing framework-dependent single-file exe (~3-5MB).

---

## File Structure

```
gui_cs/
+-- BrakeCalibrator.sln                 <-- Visual Studio solution
+-- BrakeCalibrator/
    +-- BrakeCalibrator.csproj          <-- Project file with NuGet refs
    +-- Program.cs                      <-- Entry point
    +-- MainForm.cs                     <-- Main form layout + event handlers + poll loop
    +-- HidReader.cs                    <-- RawInput.Sharp gamepad reader
    +-- CalibrationData.cs              <-- JSON model + defaults + signal processing
    +-- PicoDriveFinder.cs             <-- Find CIRCUITPY/Pico USB drive
    +-- ProfileManager.cs              <-- Profile save/load/delete on Pico drive
    +-- CurveEditorDialog.cs           <-- Interactive curve editor (ScottPlot draggable points)
```

Each file has one responsibility. The Python GUI's 1705-line monolith splits into focused classes. `CalibrationData.cs` is the pure-logic module (no UI) containing the signal processing pipeline and JSON model. `HidReader.cs` wraps RawInput.Sharp into a simple API matching the Python `PicoReader`. `PicoDriveFinder.cs` and `ProfileManager.cs` handle file I/O. The two form files handle UI only.

---

### Task 1: Project Scaffolding + Build Verification

**Files:**
- Create: `gui_cs/BrakeCalibrator.sln`
- Create: `gui_cs/BrakeCalibrator/BrakeCalibrator.csproj`
- Create: `gui_cs/BrakeCalibrator/Program.cs`
- Create: `gui_cs/BrakeCalibrator/MainForm.cs` (minimal stub)

- [ ] **Step 1: Create project directory**

```
powershell
mkdir "C:\aaa\code\Pico_Brake_Contoller\gui_cs\BrakeCalibrator"
```

- [ ] **Step 2: Create BrakeCalibrator.csproj with NuGet references**

```
xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWindowsForms>true</UseWindowsForms>
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>false</SelfContained>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ScottPlot" Version="5.0.*" />
    <PackageReference Include="ScottPlot.WinForms" Version="5.0.*" />
    <PackageReference Include="RawInput.Sharp" Version="0.0.3" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create Program.cs**

```
csharp
namespace BrakeCalibrator;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
```

- [ ] **Step 4: Create minimal MainForm.cs**

```
csharp
using System.Windows.Forms;

namespace BrakeCalibrator;

public class MainForm : Form
{
    public MainForm()
    {
        Text = "Brake Controller Calibrator";
        Size = new System.Drawing.Size(900, 600);
        MinimumSize = new System.Drawing.Size(850, 550);
    }
}
```

- [ ] **Step 5: Create solution and add project**

```
powershell
cd "C:\aaa\code\Pico_Brake_Contoller\gui_cs"
dotnet new sln -n BrakeCalibrator
dotnet sln add BrakeCalibrator\BrakeCalibrator.csproj
```

- [ ] **Step 6: Restore and build**

```
powershell
cd "C:\aaa\code\Pico_Brake_Contoller\gui_cs\BrakeCalibrator"
dotnet restore
dotnet build --configuration Release
```

Expected: Build succeeds. Output at `bin/Release/net8.0-windows/BrakeCalibrator.exe`.

- [ ] **Step 7: Verify single-file publish**

```
powershell
dotnet publish --configuration Release -p:PublishSingleFile=true --self-contained false -o publish
```

Expected: `publish/BrakeCalibrator.exe` ~3-5MB.

- [ ] **Step 8: Commit**

```
git add gui_cs/
git commit -m "feat(gui_cs): scaffold C# WinForms project with ScottPlot + RawInput.Sharp"
```

---

### Task 2: CalibrationData Model + Signal Processing

**Files:**
- Create: `gui_cs/BrakeCalibrator/CalibrationData.cs`

Pure-logic class with no UI dependencies. Mirrors the Python `_compute_preview`, `_interpolate_custom`, `_build_cal_dict`, and `_apply_cal_dict` exactly.

Contains:
- `ChannelCal` class: per-channel calibration with `ProcessRaw(int rawAdc)` implementing clamp > normalize > deadzone > bite > curve > EMA > invert pipeline. Also has `ApplyCurve`, `InterpolateCustom`, `ResetEma`, `Clone`.
- `CalibrationData` class: flat property model matching calibration.json format (all brake + throttle fields). `SyncToChannels()` copies flat props to `BrakeChannel`/`ThrottleChannel` (preserving EMA state). `SyncFromChannels()` copies back. `FromJson(string)` / `ToJson()` for JSON serialization via System.Text.Json.
- Static presets: `CurvePresets` dict, `CurveTypes` array, `OversampleOptions` array matching Python DEFAULTS/CURVE_PRESETS exactly.

Key implementation details (from Python calibrator.py analysis):
- ProcessRaw: rawMaxEff = RawMin + (RawMax - RawMin) * min(Saturation, 1.0); deadzoneFrac = Deadzone / (RawMax - RawMin); bite: if normalized < BitePoint then 0, else (normalized - BitePoint) / (1 - BitePoint); EMA: alpha = 1 - min(Smoothing, 0.95)
- Curve types: "linear" (identity), "progressive" (t^power), "aggressive" (t^(1/power)), "custom" (piecewise-linear through CurvePoints)
- InterpolateCustom: binary search through sorted control points, linear interpolation between adjacent points
- Curve presets: linear=[[0,0],[1,1]], progressive=[[0,0],[0.25,0.06],[0.5,0.25],[0.75,0.56],[1,1]], aggressive=[[0,0],[0.25,0.44],[0.5,0.75],[0.75,0.94],[1,1]], S-curve=[[0,0],[0.25,0.1],[0.5,0.5],[0.75,0.9],[1,1]]
- Default values: raw_min=2000, raw_max=56000, deadzone=300, smoothing=0.3, saturation=1.0, bite_point=0.0, invert=false, oversample=16, throttle_smoothing=0.2, throttle_sensor="auto"

- [ ] **Step 1: Create CalibrationData.cs with full model and signal processing** (~200 lines)

- [ ] **Step 2: Build**

```
powershell
cd "C:\aaa\code\Pico_Brake_Contoller\gui_cs\BrakeCalibrator"
dotnet build --configuration Release
```

- [ ] **Step 3: Commit**

```
git add gui_cs/BrakeCalibrator/CalibrationData.cs
git commit -m "feat(gui_cs): add CalibrationData model and signal processing pipeline"
```

---

### Task 3: HidReader -- Raw Input Gamepad Reader

**Files:**
- Create: `gui_cs/BrakeCalibrator/HidReader.cs`

Wraps RawInput.Sharp to enumerate and read from HID gamepads. Replaces the Python `PicoReader` class. Reads the same 4 axes (X=brake, Y=raw, Z=throttle, Rz=raw throttle) as float 0.0-1.0.

Key implementation:
- `Register(IntPtr windowHandle)`: calls `RawInputDevice.RegisterDevice(HidUsageAndPage.GamePad, RawInputDeviceFlags.InputSink, windowHandle)` -- must be called after window handle is created
- `ScanDevices()`: `RawInputDevice.GetDevices().OfType<RawInputHid>()` filtered by GamePad usage
- `AutoSelectPico()`: find device with "pico" in name, fallback to first device
- `ProcessMessage(ref Message m)`: handles WM_INPUT (0x00FF), parses the 8-byte HID report as 4 x uint16 LE, stores in `_axisValues` dict
- Read methods: `ReadBrake()`, `ReadRawAdc()`, `ReadRawAdcInt()`, `ReadThrottle()`, `ReadThrottleRaw()`, `ReadThrottleRawInt()` -- same API surface as Python PicoReader

The Pico sends: X (axis 0) = processed brake, Y (axis 1) = raw brake ADC, Z (axis 2) = processed throttle, Rz (axis 3) = raw throttle ADC. All uint16 0-65535. Read as float 0.0-1.0 by dividing by 65535.

- [ ] **Step 1: Create HidReader.cs** (~90 lines)

- [ ] **Step 2: Build**

```
powershell
cd "C:\aaa\code\Pico_Brake_Contoller\gui_cs\BrakeCalibrator"
dotnet build --configuration Release
```

- [ ] **Step 3: Commit**

```
git add gui_cs/BrakeCalibrator/HidReader.cs
git commit -m "feat(gui_cs): add HidReader wrapping RawInput.Sharp for gamepad input"
```

---

### Task 4: PicoDriveFinder + ProfileManager

**Files:**
- Create: `gui_cs/BrakeCalibrator/PicoDriveFinder.cs`
- Create: `gui_cs/BrakeCalibrator/ProfileManager.cs`

PicoDriveFinder:
- Uses P/Invoke `GetVolumeInformationW` from kernel32.dll to check volume names
- Primary search: find drive with volume name "CIRCUITPY"
- Fallback 1: find drive with `boot_out.txt` (CircuitPython always creates this file)
- Fallback 2: find drive with "Pico" or "BRAKE" in volume name (for C++ firmware MSC)
- Returns drive root path (e.g., "D:\") or null

ProfileManager:
- Constructor takes picoDrive path (from PicoDriveFinder)
- `ProfilesDir` = picoDrive + "profiles"
- `ListProfiles()`: scan profiles/*.json, return names without extension
- `SaveProfile(name, cal)`: sanitize name (alphanumeric/space/dash/underscore only), create profiles dir, write cal.ToJson()
- `LoadProfile(name)`: read file, CalibrationData.FromJson()
- `DeleteProfile(name)`: File.Delete()
- `SaveCalibration(cal)`: write calibration.json to picoDrive root

- [ ] **Step 1: Create PicoDriveFinder.cs** (~60 lines)

- [ ] **Step 2: Create ProfileManager.cs** (~70 lines)

- [ ] **Step 3: Build**

```
powershell
cd "C:\aaa\code\Pico_Brake_Contoller\gui_cs\BrakeCalibrator"
dotnet build --configuration Release
```

- [ ] **Step 4: Commit**

```
git add gui_cs/BrakeCalibrator/PicoDriveFinder.cs gui_cs/BrakeCalibrator/ProfileManager.cs
git commit -m "feat(gui_cs): add PicoDriveFinder and ProfileManager for file I/O"
```

---

### Task 5: CurveEditorDialog with ScottPlot Draggable Points

**Files:**
- Create: `gui_cs/BrakeCalibrator/CurveEditorDialog.cs`

Interactive curve editor using ScottPlot 5 scatter plot. Replaces the Python Tkinter Canvas hand-rolled implementation. Features:
- Drag-to-edit control points (left mouse)
- Click empty space to add a new point
- Right-click to delete a point (endpoints protected)
- Preset buttons: Linear, Progressive, Aggressive, S-curve (sets curve type + control points)
- Progressive/Aggressive power sliders (1.1-5.0 range)
- Apply/Cancel buttons
- Dark theme matching the main graph

Implementation approach:
- FormsPlot control for the graph area
- Two scatter plots: `_scatterCurve` (the curve line) and `_scatterPts` (the draggable control points as large markers)
- Mouse events: MouseDown (detect nearest point for drag or add new), MouseMove (update dragged point position with constraints), MouseUp (release drag)
- Point constraints: endpoints (index 0 and last) fixed at x=0 and x=1, interior points constrained between neighbors with 0.01 minimum gap, y clamped 0-1
- When dragging changes points, auto-switch curve type to "custom"
- Redraw() regenerates curve coordinates based on SelectedCurve type: "custom" = lines through points, "progressive" = t^power sampled at 61 points, "aggressive" = t^(1/power) at 61 points, "linear" = diagonal

Result properties: `SelectedCurve`, `ProgressivePower`, `AggressivePower`, `EditedPoints`, `Applied` (bool)

- [ ] **Step 1: Create CurveEditorDialog.cs** (~200 lines)

- [ ] **Step 2: Build**

```
powershell
cd "C:\aaa\code\Pico_Brake_Contoller\gui_cs\BrakeCalibrator"
dotnet build --configuration Release
```

- [ ] **Step 3: Commit**

```
git add gui_cs/BrakeCalibrator/CurveEditorDialog.cs
git commit -m "feat(gui_cs): add CurveEditorDialog with ScottPlot draggable points"
```

---

### Task 6: MainForm -- Complete UI + Poll Loop

**Files:**
- Modify: `gui_cs/BrakeCalibrator/MainForm.cs` (replace stub with full implementation)

This is the main form combining all components:

**Layout:** SplitContainer with fixed right panel:
- Left panel: ScottPlot FormsPlot (live graph) + info labels (Raw ADC, Brake, Throttle) + device selector ComboBox
- Right panel (scrollable): Auto Calibrate group, Profiles group, Throttle group, Brake group, Save to Pico button

**Graph (4 lines matching Python):**
- Raw ADC (blue #4488ff) -- raw brake sensor values 0-1
- Preview (green #44ff44) -- locally computed with current slider settings
- Game Input (red #ff4444) -- actual Pico output (processed by firmware)
- Throttle (orange #ff8800) -- only when throttle enabled

**Right panel controls (matching Python exactly):**
- Auto Calibrate: button + label for countdown/capture status
- Profiles: ComboBox for profile names + Save/Load/Delete buttons
- Throttle: enable checkbox, raw min/max NumericUpDown + Set buttons, curve ComboBox + Edit button, smoothing TrackBar (0-95 -> 0.0-0.95), deadzone TrackBar (0-1000), invert checkbox, saturation TrackBar (10-100 -> 0.1-1.0), bite point TrackBar (0-50 -> 0.0-0.5)
- Brake: same controls as throttle (except enable checkbox + oversample ComboBox [1,4,16,64])
- Save to Pico button

**Poll loop (30Hz via Timer):**
1. SyncCalFromUi(): read all UI control values into _cal properties then SyncToChannels()
2. Read HID axes via HidReader
3. Compute preview: _cal.BrakeChannel.ProcessRaw(rawInt) for brake, similar for throttle
4. Update history lists (max 200 entries, FIFO)
5. Update info labels
6. ProcessAutoCal(): state machine with countdown(3s) > capture(5s) > done
7. UpdateGraph(): set scatter data arrays + Refresh
8. UpdateStatus()

**Key events:**
- OnLoad: register RawInput, auto-select Pico, start poll timer
- WndProc: forward WM_INPUT to HidReader
- OnThrottleToggle: show/hide throttle controls, clear histories
- ShowCurveEditor("brake"/"throttle"): open CurveEditorDialog, apply result on OK
- SaveCalibration: _cal.SyncFromChannels() then ProfileManager.SaveCalibration()
- Save/Load/Delete Profile: via ProfileManager

**Auto-calibration state machine (matching Python):**
- StartAutoCal: if throttle enabled, ask brake/throttle target via MessageBox
- countdown phase: show "Release {target}..." with countdown, transition after 3s
- capture phase: collect raw ADC integers, show remaining time, after 5s compute min/max then set entries
- done: re-enable button

- [ ] **Step 1: Replace MainForm.cs with full implementation** (~450 lines)

- [ ] **Step 2: Build and run**

```
powershell
cd "C:\aaa\code\Pico_Brake_Contoller\gui_cs\BrakeCalibrator"
dotnet build --configuration Release
```

Expected: Compiles without errors. Run exe to verify window appears with graph and all controls.

- [ ] **Step 3: Commit**

```
git add gui_cs/BrakeCalibrator/MainForm.cs
git commit -m "feat(gui_cs): implement MainForm with live graph, controls, and poll loop"
```

---

### Task 7: Integration Testing + Publish

**Files:**
- Modify: `gui_cs/BrakeCalibrator/MainForm.cs` (bug fixes if needed)

- [ ] **Step 1: Connect to Pico and verify HID input flows through graph**

1. Flash Pico with CircuitPython or C++ firmware
2. Run BrakeCalibrator.exe
3. Verify: device auto-selects Pico, raw ADC line moves when pressing pedal
4. Verify: preview line applies current settings in real-time
5. Verify: game input line matches Pico's processed output

- [ ] **Step 2: Test auto-calibration flow**

1. Click Auto Calibrate
2. Release pedal during countdown
3. Press firmly during capture
4. Verify: min/max entries update correctly

- [ ] **Step 3: Test curve editor**

1. Click Edit on brake curve
2. Switch between presets (Linear, Progressive, Aggressive, S-curve)
3. Drag a control point
4. Add a new point by clicking empty space
5. Delete a point by right-clicking
6. Click Apply -- verify curve type switches to "custom"

- [ ] **Step 4: Test throttle toggle**

1. Check "Use Throttle"
2. Verify: throttle controls visible, orange line appears on graph
3. Uncheck -- verify controls hidden, throttle line disappears

- [ ] **Step 5: Test profiles**

1. Save a profile with a name
2. Change some settings
3. Load the saved profile -- verify settings restored
4. Delete the profile

- [ ] **Step 6: Test Save to Pico**

1. Click Save to Pico
2. Verify: calibration.json written to CIRCUITPY (or Pico MSC) drive
3. Press RESET on Pico -- verify it picks up new calibration

- [ ] **Step 7: Publish single-file exe**

```
powershell
cd "C:\aaa\code\Pico_Brake_Contoller\gui_cs\BrakeCalibrator"
dotnet publish --configuration Release -p:PublishSingleFile=true --self-contained false -o publish
```

Expected: `publish/BrakeCalibrator.exe` ~3-5MB.

- [ ] **Step 8: Commit final**

```
git add gui_cs/
git commit -m "feat(gui_cs): complete C# WinForms calibration GUI"
```

---

## Python to C# Migration Mapping

| Python | C# |
|--------|-----|
| `pygame-ce` PicoReader | `RawInput.Sharp` HidReader |
| `tkinter.Canvas` graph | ScottPlot 5 `FormsPlot` |
| `tkinter.Scale` | `TrackBar` |
| `tkinter.Entry` (int) | `NumericUpDown` |
| `tkinter.ttk.Combobox` | `ComboBox` |
| `tkinter.ttk.Checkbutton` | `CheckBox` |
| `json.load()` / `json.dump()` | `System.Text.Json` |
| `find_circuitpy_drive()` (ctypes) | `PicoDriveFinder` (P/Invoke) |
| `_compute_preview()` | `ChannelCal.ProcessRaw()` |
| `_interpolate_custom()` | `ChannelCal.InterpolateCustom()` |
| `_show_curve_dialog` (Tkinter Canvas) | `CurveEditorDialog` (ScottPlot scatter) |
| `_poll_loop` (after 33ms) | `Timer.Tick` at 33ms |
| Profile save/load/delete | `ProfileManager` |
| Auto-calibration state machine | Same phases in `ProcessAutoCal()` |

## Known Differences from Python GUI

1. **Curve editor**: ScottPlot scatter with draggable markers instead of hand-rolled Canvas drawing. Same interaction (drag/add/delete), different rendering.
2. **Tooltip system**: WinForms `ToolTip` component instead of custom `ToolTip` class. Works the same way.
3. **Auto-cal target dialog**: Uses `MessageBox.Show` with YesNoCancel instead of custom Tkinter dialog. Simpler but functional.
4. **Profile name input**: Uses `Microsoft.VisualBasic.Interaction.InputBox` (available in .NET without extra NuGet) instead of `simpledialog.askstring`.
5. **No tooltip class needed**: WinForms `ToolTip` component handles hover automatically -- no custom `ToolTip` class.