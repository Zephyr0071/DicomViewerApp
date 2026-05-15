# Medical 2D/3D Flow Viewer

A high-performance .NET 8.0 desktop application designed for the visualization and analysis of complex medical imaging data, including standard DICOM series, HDF5-based Vector Flow Mapping (VFM), and massive 3D CFD (Computational Fluid Dynamics) surface meshes.

## 🌟 Key Features

### 1. Unified 2D Viewing

* **DICOM Support:** Full support for `.dcm`, `.dicom`, and `.ima` files with real-time Window Center (WC) and Window Width (WW) adjustments.
* **Vector Flow Mapping (VFM):** Advanced processing for HDF5 (`.h5`, `.vfm`) data in both Polar and Cartesian coordinates.
* **iTFlow Integration:** Native support for proprietary `.itflow2` and `.itsp` project formats.
* **Multi-Channel visualization:** Toggle between Tissue (Cine/M-Mode) and Velocity channels (Anteroposterior, Superior-Inferior, Right-Left, and Power).

### 2. High-Performance 3D Surface Viewer

* **Massive File Support:** Optimized to handle `.itsf` files exceeding 1GB.
* **Surface Shell Extraction:** Utilizes a custom-built ParaView-style algorithm to strip internal tetrahedrons/cells, rendering only the "outer skin" to ensure 60+ FPS even with millions of points.
* **Smooth Shading:** Automated normal computation for realistic 3D volume lighting and shadows.
* **ParaView Controls:** Industry-standard 3D navigation gestures (Left-click orbit, Right-click pan).

### 3. Analysis & Reporting

* **PDF Reports:** Instant generation of medical reports including metadata and findings via QuestPDF.
* **Frame Pinning:** Bookmark specific slices or frames for quick comparison and review.
* **Snapshots:** High-resolution PNG exports of any 2D or 3D view.

---

## 🎮 Navigation & Controls

### 2D View

| Input | Action |
| --- | --- |
| **Left Click + Drag** | Pan / Move image |
| **Right Click + Drag** | Adjust Window/Level (DICOM only) |
| **Ctrl + Scroll** | Zoom In/Out |
| **Scroll Wheel** | Navigate frames/slices |

### 3D View (ParaView Style)

| Input | Action |
| --- | --- |
| **Left Click + Drag** | **Rotate / Orbit** around the model |
| **Right Click + Drag** | **Pan / Slide** the camera |
| **Scroll Wheel** | **Zoom** toward/away from the focal point |
| **ViewCube** | Quick orientation snapping |

---

## 📂 Supported File Formats

* **DICOM:** `.dcm`, `.dicom`, `.ima`
* **HDF5/VFM:** `.h5`, `.vfm`
* **3D Surfaces:** `.itsf` (Native iTFlow), `.stl`, `.ply`
* **iTFlow Projects:** `.itflow2`, `.itsp`
* **Images:** `.png`, `.jpg`, `.bmp`

---

## 🛠️ Tech Stack

* **Framework:** .NET 8.0 Windows
* **UI:** Windows Forms + WPF (ElementHost)
* **DICOM Engine:** [fo-dicom](https://github.com/fo-dicom/fo-dicom)
* **3D Rendering:** [Helix Toolkit (WPF)](https://github.com/helix-toolkit/helix-toolkit)
* **Data Handling:** PureHDF, protobuf-net, Newtonsoft.Json
* **PDF Engine:** QuestPDF

---

## 🚀 Getting Started

### Prerequisites

* Visual Studio 2022
* .NET 8.0 SDK
* Windows 10/11 (x64)

### Installation

1. Clone the repository:
```bash
git clone https://github.com/YourUsername/DicomViewerApp.git

```


2. Open `DicomViewerApp.sln` in Visual Studio.
3. Restore NuGet packages.
4. Build and Run in **Release / x64** mode for best 3D performance.

---

## 💡 Performance Optimization Note

This application includes a **Smart Surface Extractor** for `.itsf` files. Standard 3D engines often choke on CFD data because it contains millions of internal points. Our parser identifies hidden internal faces and discards them before the data ever reaches the GPU, reducing the triangle count by up to 95% without losing any visual detail of the arterial wall or surface flow.
