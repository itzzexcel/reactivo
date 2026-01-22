# Reactivo

Reactivo is a real-time audio visualization tool that captures system audio, performs frequency analysis, and broadcasts the data to a web-based frontend for immersive visualization.

## Features 

- **Real-time Audio Capture**: Uses WASAPI Loopback to capture "What You Hear" from your system's default audio output.
- **Frequency Analysis**: Performs FFT (Fast Fourier Transform) to analyze audio spectrum.
  - **Bass Detection**: Monitors 20Hz - 200Hz range.
  - **Treble Detection**: Monitors 4kHz - 20kHz range.
- **Beat Detection**: Estimates BPM (Beats Per Minute) based on energy surges.
- **WebSocket Broadcast**: Streams analysis data (JSON) to connected clients in real-time.
- **Web Visualization**: Includes a browser-based client that renders dynamic visual effects reacting to music.

## Prerequisites

- **OS**: Windows (Required for WASAPI Loopback Capture).
- **Runtime**: .NET 9.0 SDK or Runtime.
- **Browser**: A modern web browser (Chrome, Firefox, Edge) for the visualization client.

## Installation & Setup

1. **Clone the repository**
   ```bash
   git clone https://github.com/itzzexcel/reactivo.git
   cd reactivo
   ```

2. **Build the project**
   ```bash
   dotnet build -c Release
   ```

3. **Run the Server**
   ```bash
   dotnet run --project reactivo.csproj
   # Or run the executable directly from bin/Release/net9.0-windows/
   ```
   The server will start monitoring audio and host a WebSocket server at `ws://localhost:5343`.

4. **Launch the Visualizer**
   Open the file `web-test/index.html` in your web browser. It will automatically connect to the WebSocket server and start visualizing the audio.

## Project Structure

- **`Classes/`**: Contains the core application logic.
  - [`FreqDetect.cs`](Classes/FreqDetect.cs): Handles audio capture (NAudio) and FFT analysis.
  - [`WebScket.cs`](Classes/WebScket.cs): Manages the WebSocket server and client connections.
  - [`BPMDetect.cs`](Classes/BPMDetect.cs): Logic for beat detection and BPM calculation.
- **`web-test/`**: Contains the frontend client.
  - [`index.html`](web-test/index.html): Main visualization interface using HTML5 and JavaScript.
- **`Program.cs`**: Entry point of the console application.

<hr/>

— Made with love by Excel. <3
