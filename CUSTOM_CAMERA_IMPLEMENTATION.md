# Custom Native Android Camera with Scanbot Document Detection

## Overview

This implementation demonstrates how to integrate a native Android Camera2 API with Scanbot's document detection logic in the scanbot-sdk-maui-example project. The solution provides a custom camera preview window with real-time document detection, automatic snap functionality, and a simple polygon overlay showing detected document boundaries.

## Architecture

The implementation consists of the following components:

### 1. **CustomCameraPreviewActivity.cs** (`NET/ScanbotSdkExample.Droid/Activities/`)
The main activity that manages:
- Camera2 API initialization and lifecycle management
- Real-time frame processing for document detection
- User interface controls (shutter button, flash toggle, auto-snap toggle)
- Document capture and storage integration with Scanbot SDK

**Key Features:**
- Native Camera2 API for hardware camera access
- Background thread processing to avoid UI blocking
- Real-time frame capture and processing at 5 FPS (configurable)
- Auto-snapping when document is detected in good condition
- Flash control and auto-focus configuration
- Seamless integration with Scanbot DocumentScanner API

### 2. **DocumentPolygonOverlayView.cs** (`NET/ScanbotSdkExample.Droid/Views/`)
A custom Android View that renders the detected document polygon:
- Displays detected document corners with visual markers
- Shows polygon outline (green for good detection, red for poor)
- Semi-transparent fill showing the document area
- Updates in real-time as detection results change

**Visual Features:**
- Green polygon when detection is optimal
- Red polygon when document detection is poor
- White corner point markers for precise corner visualization
- Smooth rendering without blocking the camera preview

### 3. **DocumentDetectionHandler.cs** (`NET/ScanbotSdkExample.Droid/Views/`)
Manages document detection results and UI updates:
- Processes detection results from Scanbot SDK
- Updates polygon overlay with normalized detection points
- Handles user guidance messages (e.g., "Move closer", "Poor light")
- Throttles guidance updates to prevent excessive UI thread calls
- Provides clean interface for detection callbacks

### 4. **custom_camera_preview.xml** (`NET/ScanbotSdkExample.Droid/Resources/layout/`)
Layout definition for the camera preview interface:
- TextureView for native camera rendering
- Custom DocumentPolygonOverlayView for detection visualization
- Control buttons (Flash, Auto-snap toggle)
- User guidance text display
- Shutter button for manual capture
- Progress bar for image processing feedback

## How It Works

### Camera Initialization Flow:
1. Activity creates and initializes Camera2 manager
2. Requests camera permissions (Android 6.0+)
3. Opens the back camera device
4. Creates a capture session with TextureView as preview target
5. Sets up continuous auto-focus and auto-exposure

### Real-Time Detection Flow:
1. Camera delivers frames to TextureView
2. OnSurfaceTextureFrameAvailable is called for each frame
3. If auto-snapping is enabled, frame is extracted as Bitmap
4. Bitmap is processed by Scanbot DocumentScanner on background thread
5. Detection results (polygon points, status) are returned
6. UI thread is notified to update polygon overlay and guidance text
7. If detection is good and auto-snap enabled, image is automatically captured

### Capture & Storage Flow:
1. User taps shutter button or auto-snap triggers
2. Current TextureView frame is captured as Bitmap
3. Document is detected and stored in Scanbot DocumentApi
4. Document UUID is passed to PagePreviewActivity for review/editing
5. Activity closes and returns to MainActivity

## Usage

### Launching the Custom Camera Preview:
The "Custom Camera Preview (Native)" button has been added to MainActivity's scanner section. When tapped:
1. License is validated
2. CustomCameraPreviewActivity is launched
3. User can point camera at documents
4. Real-time detection polygon appears
5. Document is either auto-snapped or manually captured
6. PagePreviewActivity opens for document review

```csharp
// In MainActivity.DocumentScanner.cs
private void LaunchCustomCameraPreview()
{
    if (!CheckLicense())
    {
        return;
    }

    Intent intent = new Intent(this, typeof(CustomCameraPreviewActivity));
    StartActivityForResult(intent, ScanDocumentRequestCode);
}
```

## Key Configuration Parameters

In `CustomCameraPreviewActivity.cs`:

```csharp
// Frame processing frequency (reduce to lower CPU usage)
private const float DetectionFps = 5f; // Process 5 frames per second

// Corner visualization size
private float _cornerRadius = 15f;

// Polygon stroke width
private float _lineStrokeWidth = 3f;
```

## Permissions

The AndroidManifest.xml includes:
- `android.permission.CAMERA` - For camera access
- `android.permission.READ_EXTERNAL_STORAGE` - For image handling
- `android.permission.WRITE_EXTERNAL_STORAGE` - For document storage

The activity is registered with:
- `android:exported="false"` - Not exported to other apps
- `android:screenOrientation="portrait"` - Portrait mode optimization
- Theme: `@style/Theme.AppCompat`

## Integration with Scanbot SDK

### Document Scanner API Usage:
```csharp
// Create document scanner instance
var documentScanner = _scanbotSdk.CreateDocumentScanner();

// Process frame
var detectionResult = documentScanner.ScanFromBitmap(frameBitmap);

// Create document and add page
var document = _scanbotSdk.DocumentApi.CreateDocument(0);
document.AddPage(capturedBitmap);

// Add detected polygon
if (detectionResult != null)
{
    document.PageAtIndex(0).Polygon = detectionResult.PointsNormalized;
}
```

### Detection Status Handling:
- `DocumentDetectionStatus.Ok` - Document ready to capture
- `DocumentDetectionStatus.OkButTooSmall` - Move closer to document
- `DocumentDetectionStatus.OkButBadAngles` - Adjust camera angle
- `DocumentDetectionStatus.OkButBadAspectRatio` - Rotate device
- `DocumentDetectionStatus.ErrorNothingDetected` - No document visible
- `DocumentDetectionStatus.ErrorTooNoisy` - Background too noisy
- `DocumentDetectionStatus.ErrorTooDark` - Insufficient lighting

## Performance Considerations

1. **Frame Processing Rate**: Set to 5 FPS by default to balance responsiveness and CPU usage. Adjust `DetectionFps` constant for your needs.

2. **Background Thread**: All heavy processing (document detection, bitmap operations) happens on a background thread to keep UI responsive.

3. **Memory Management**: Large frames are processed and disposed immediately to prevent memory leaks. Use `bitmap.Dispose()` after processing.

4. **Auto-Focus**: Continuous auto-focus is enabled for better document detection in varying lighting conditions.

## Debugging Tips

1. **Camera Not Opening**: Verify CAMERA permission is granted. Use `adb logcat` to check for camera access errors.

2. **No Detection**: Ensure proper lighting. Document detection works best with clear, well-lit documents.

3. **Performance Issues**: Reduce `DetectionFps` value or optimize bitmap size. The SDK can handle various input sizes.

4. **UI Updates**: All UI changes from background threads are wrapped in `RunOnUiThread()` to avoid cross-thread exceptions.

## Future Enhancements

Possible improvements for this implementation:

1. **Multi-page Support**: Allow capturing multiple pages in one session
2. **Aspect Ratio Constraints**: Add configurable aspect ratio requirements
3. **Page Rotation**: Add automatic page rotation detection
4. **Batch Processing**: Process multiple frames concurrently
5. **Manual Polygon Adjustment**: Let users adjust detected polygon corners
6. **Preview Filters**: Add document enhancement filters (B&W, grayscale, etc.)
7. **Gesture Controls**: Implement zoom and focus gestures
8. **Performance Metrics**: Display FPS and processing time for optimization

## References

- **Scanbot SDK Documentation**: https://docs.scanbot.io/maui/document-scanner-sdk/
- **Android Camera2 API**: https://developer.android.com/reference/android/hardware/camera2/Camera
- **Scanbot GitHub Samples**: https://github.com/doo/scanbot-sdk-example-android

## Files Created/Modified

### New Files:
- `NET/ScanbotSdkExample.Droid/Activities/CustomCameraPreviewActivity.cs`
- `NET/ScanbotSdkExample.Droid/Views/DocumentPolygonOverlayView.cs`
- `NET/ScanbotSdkExample.Droid/Views/DocumentDetectionHandler.cs`
- `NET/ScanbotSdkExample.Droid/Resources/layout/custom_camera_preview.xml`

### Modified Files:
- `NET/ScanbotSdkExample.Droid/AndroidManifest.xml` - Added activity registration
- `NET/ScanbotSdkExample.Droid/MainActivity.cs` - Added menu button
- `NET/ScanbotSdkExample.Droid/MainActivity.DocumentScanner.cs` - Added launch method

## Testing

To test the implementation:

1. Build the project:
   ```bash
   dotnet build NET/ScanbotSdkExample.Droid/ScanbotSdkExample.Droid.csproj -f net9.0-android
   ```

2. Deploy to device/emulator:
   ```bash
   dotnet build NET/ScanbotSdkExample.Droid/ScanbotSdkExample.Droid.csproj -f net9.0-android -t:Run
   ```

3. In the app, tap "Custom Camera Preview (Native)" button

4. Point camera at a document and verify:
   - Real-time polygon appears
   - Guidance text updates appropriately
   - Auto-snap triggers or manual shutter works
   - Document is saved and PagePreviewActivity opens

## License

This implementation integrates with Scanbot SDK which requires a valid license. Ensure your license key is configured in `MainApplication.cs`.
