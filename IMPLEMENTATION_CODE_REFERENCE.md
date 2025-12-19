# Implementation Code Reference

## Quick Reference Guide

This document provides quick reference to the key code sections for the custom native Android camera implementation with Scanbot document detection.

## 1. Launch From MainActivity

**File**: `MainActivity.DocumentScanner.cs`

```csharp
/// <summary>
/// Launches the custom native Android camera preview activity with Scanbot document detection.
/// </summary>
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

## 2. Camera Initialization

**File**: `CustomCameraPreviewActivity.cs` - `OnCreate()` method

```csharp
protected override void OnCreate(Bundle savedInstanceState)
{
    SupportRequestWindowFeature(WindowCompat.FeatureActionBarOverlay);
    base.OnCreate(savedInstanceState);
    
    SetContentView(Resource.Layout.custom_camera_preview);
    
    // Initialize Scanbot SDK
    _scanbotSdk = new ScanbotSDK(this);
    _documentScanner = _scanbotSdk.CreateDocumentScanner();
    
    // Initialize UI
    InitializeUIElements();
    
    // Request permissions and initialize camera
    if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
    {
        if (CheckSelfPermission(Android.Manifest.Permission.Camera) != Android.Content.PM.Permission.Granted)
        {
            RequestPermissions(
                new[] { Android.Manifest.Permission.Camera },
                CameraPermissionRequestCode
            );
        }
        else
        {
            InitializeCamera();
        }
    }
    else
    {
        InitializeCamera();
    }
}
```

## 3. Real-Time Frame Processing

**File**: `CustomCameraPreviewActivity.cs` - `OnSurfaceTextureFrameAvailable()` method

```csharp
public void OnSurfaceTextureFrameAvailable(SurfaceTexture surface)
{
    // Real-time frame processing for document detection
    if (!_autoSnappingEnabled || _isPhotoCapturing || !_isCameraInitialized)
    {
        return;
    }

    long currentTime = JavaSystem.CurrentTimeMillis();
    if (currentTime - _lastFrameProcessingTime < _frameProcessingIntervalMs)
    {
        return;
    }

    _lastFrameProcessingTime = currentTime;
    
    // Process frame in background thread
    _backgroundHandler?.Post(() =>
    {
        try
        {
            Bitmap bitmap = _textureView.GetBitmap();
            if (bitmap != null)
            {
                ProcessFrameForDocumentDetection(bitmap);
                bitmap.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error("CustomCamera", $"Frame processing error: {ex.Message}");
        }
    });
}
```

## 4. Document Detection Logic

**File**: `CustomCameraPreviewActivity.cs` - `ProcessFrameForDocumentDetection()` method

```csharp
private void ProcessFrameForDocumentDetection(Bitmap frameBitmap)
{
    try
    {
        if (_documentScanner == null || frameBitmap == null)
        {
            return;
        }

        // Run Scanbot document detection
        var detectionResult = _documentScanner.ScanFromBitmap(frameBitmap);
        
        RunOnUiThread(() =>
        {
            if (detectionResult != null)
            {
                // Update polygon overlay with detected points
                _polygonOverlay.SetDetectedPolygon(detectionResult.PointsNormalized);
                _detectionHandler.ShowDetectionStatus(detectionResult);
                
                // Auto-snap if conditions are met
                if (_autoSnappingEnabled && 
                    detectionResult.Status == DocumentDetectionStatus.Ok &&
                    !_isPhotoCapturing)
                {
                    CaptureDocument();
                }
            }
            else
            {
                _polygonOverlay.ClearPolygon();
                _detectionHandler.ShowMessage("No document detected");
            }
        });
    }
    catch (Exception ex)
    {
        Log.Error("CustomCamera", $"Detection error: {ex.Message}");
    }
}
```

## 5. Document Capture & Storage

**File**: `CustomCameraPreviewActivity.cs` - `ProcessCapturedImage()` method

```csharp
private void ProcessCapturedImage(Bitmap capturedBitmap)
{
    try
    {
        // Detect document in the captured image
        var detectionResult = _documentScanner.ScanFromBitmap(capturedBitmap);

        // Create document in Scanbot storage
        var document = _scanbotSdk.DocumentApi.CreateDocument(0);
        document.AddPage(capturedBitmap);

        // Add detected polygon to the page
        if (detectionResult != null)
        {
            document.PageAtIndex(0).Polygon = detectionResult.PointsNormalized;
        }

        // Navigate to preview
        RunOnUiThread(() =>
        {
            _imageProcessingProgress.Visibility = ViewStates.Gone;
            
            var intent = PagePreviewActivity.CreateIntent(this, document.Uuid);
            StartActivity(intent);
            
            Finish();
        });
    }
    catch (Exception ex)
    {
        Log.Error("CustomCamera", $"Image processing error: {ex.Message}");
        _isPhotoCapturing = false;
        RunOnUiThread(() =>
        {
            _imageProcessingProgress.Visibility = ViewStates.Gone;
            ShowMessage("Failed to process image");
        });
    }
}
```

## 6. Polygon Overlay Drawing

**File**: `DocumentPolygonOverlayView.cs` - `OnDraw()` method

```csharp
protected override void OnDraw(Canvas canvas)
{
    base.OnDraw(canvas);

    if (_detectedPoints == null || _detectedPoints.Length < 4)
    {
        return;
    }

    // Draw filled polygon
    var path = new Path();
    path.MoveTo(_detectedPoints[0].X, _detectedPoints[0].Y);
    
    for (int i = 1; i < _detectedPoints.Length; i++)
    {
        path.LineTo(_detectedPoints[i].X, _detectedPoints[i].Y);
    }
    
    path.Close();
    canvas.DrawPath(path, _polygonFillPaint);

    // Draw polygon outline
    canvas.DrawPath(path, _polygonPaint);

    // Draw corner points
    foreach (var point in _detectedPoints)
    {
        canvas.DrawCircle(point.X, point.Y, _cornerRadius, _cornerRadiusPaint);
        canvas.DrawCircle(point.X, point.Y, _cornerRadius, _cornerPaint);
    }
}
```

## 7. User Guidance Display

**File**: `CustomCameraPreviewActivity.cs` - `ShowUserGuidance()` method

```csharp
private bool ShowUserGuidance(DocumentDetectionStatus status)
{
    string guidance = status switch
    {
        DocumentDetectionStatus.Ok => "Ready to capture",
        DocumentDetectionStatus.OkButTooSmall => "Move closer",
        DocumentDetectionStatus.OkButBadAngles => "Bad angle",
        DocumentDetectionStatus.OkButBadAspectRatio => "Rotate device",
        DocumentDetectionStatus.ErrorNothingDetected => "No document",
        DocumentDetectionStatus.ErrorTooNoisy => "Background too noisy",
        DocumentDetectionStatus.ErrorTooDark => "Poor lighting",
        _ => "Positioning document..."
    };

    Color guidanceColor = status == DocumentDetectionStatus.Ok ? Color.Green : Color.Red;
    
    RunOnUiThread(() =>
    {
        _userGuidanceTextView.Text = guidance;
        _userGuidanceTextView.SetTextColor(Color.White);
        _userGuidanceTextView.SetBackgroundColor(guidanceColor);
    });

    return false;
}
```

## 8. Camera Controls

### Flash Toggle
```csharp
private void ToggleFlash()
{
    if (_cameraDevice == null || _cameraCaptureSession == null)
    {
        return;
    }

    try
    {
        var requestBuilder = _cameraDevice.CreateCaptureRequest(CameraTemplate.Preview);
        requestBuilder.AddTarget(_previewSurface);
        
        _flashEnabled = !_flashEnabled;
        requestBuilder.Set(CaptureRequest.ControlFlashMode, 
            _flashEnabled ? (int)ControlFlashMode.On : (int)ControlFlashMode.Off);
        
        _cameraCaptureSession.SetRepeatingRequest(requestBuilder.Build(), null, null);
        _flashButton.Text = _flashEnabled ? "Flash: ON" : "Flash: OFF";
    }
    catch (CameraAccessException ex)
    {
        Log.Error("CustomCamera", $"Flash toggle error: {ex.Message}");
    }
}
```

### Auto-Snap Toggle
```csharp
private void ToggleAutoSnapping()
{
    _autoSnappingEnabled = !_autoSnappingEnabled;
    _autoSnappingToggleButton.Text = _autoSnappingEnabled ? "Auto: ON" : "Auto: OFF";
    
    if (!_autoSnappingEnabled)
    {
        _userGuidanceTextView.Text = "";
        _polygonOverlay.ClearPolygon();
    }
}
```

## 9. Layout XML Structure

**File**: `custom_camera_preview.xml`

```xml
<FrameLayout>
    <!-- Native camera preview -->
    <TextureView android:id="@+id/texture_view" />
    
    <!-- Detection polygon overlay -->
    <DocumentPolygonOverlayView android:id="@+id/polygon_overlay" />
    
    <!-- Top controls -->
    <LinearLayout>
        <Button android:id="@+id/auto_snapping_toggle_button" />
        <Button android:id="@+id/flash_button" />
    </LinearLayout>
    
    <!-- Guidance text -->
    <TextView android:id="@+id/user_guidance_text_view" />
    
    <!-- Processing indicator -->
    <ProgressBar android:id="@+id/image_processing_progress" />
    
    <!-- Shutter button -->
    <ImageButton android:id="@+id/shutter_button" />
</FrameLayout>
```

## 10. AndroidManifest Registration

**File**: `AndroidManifest.xml`

```xml
<activity 
    android:name="ScanbotSdkExample.Droid.Activities.CustomCameraPreviewActivity" 
    android:exported="false" 
    android:theme="@style/Theme.AppCompat"
    android:screenOrientation="portrait" />
```

## Key Concepts

### Normalized Coordinates
Scanbot returns polygon points in normalized coordinates (0-1 range). These must be converted to screen coordinates:

```csharp
public void SetDetectedPolygon(PointF[] normalizedPoints)
{
    _detectedPoints = new PointF[normalizedPoints.Length];
    for (int i = 0; i < normalizedPoints.Length; i++)
    {
        _detectedPoints[i] = new PointF(
            normalizedPoints[i].X * Width,
            normalizedPoints[i].Y * Height
        );
    }
}
```

### Background Thread Processing
All heavy operations use a background thread:

```csharp
private void StartBackgroundThread()
{
    _backgroundThread = new HandlerThread("CameraBackground");
    _backgroundThread.Start();
    _backgroundHandler = new Handler(_backgroundThread.Looper);
}

_backgroundHandler?.Post(() =>
{
    // Heavy processing here (detection, bitmap operations)
});
```

### Resource Cleanup
Proper cleanup prevents memory leaks:

```csharp
protected override void OnDestroy()
{
    _documentScanner?.Dispose();
    base.OnDestroy();
}

private void CloseCamera()
{
    try
    {
        _cameraCaptureSession?.Close();
        _cameraDevice?.Close();
        _previewSurface?.Release();
    }
    catch (Exception ex)
    {
        Log.Error("CustomCamera", $"Close camera error: {ex.Message}");
    }
}
```

## Common Adjustments

### Change Frame Processing Rate
```csharp
private const float DetectionFps = 5f; // Change this value (1-30 fps)
```

### Modify Polygon Colors
In `DocumentPolygonOverlayView.cs`:
```csharp
private const int GoodDetectionColor = unchecked((int)0xFF00FF00); // Green
private const int PoorDetectionColor = unchecked((int)0xFFFF0000);  // Red
```

### Adjust Corner Marker Size
```csharp
private float _cornerRadius = 15f; // Increase or decrease as needed
```

## Troubleshooting

| Issue | Solution |
|-------|----------|
| Camera not opening | Check CAMERA permission in manifest |
| No document detection | Ensure good lighting, clear documents |
| Performance lag | Reduce DetectionFps value |
| Crash on permission denied | RequestPermissions called with proper callback |
| Memory leaks | Ensure bitmap.Dispose() is called |
