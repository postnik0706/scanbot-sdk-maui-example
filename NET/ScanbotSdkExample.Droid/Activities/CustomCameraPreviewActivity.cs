using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.OS;
using Android.Views;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using IO.Scanbot.Sdk;
using IO.Scanbot.Sdk.Camera;
using IO.Scanbot.Sdk.Document;
using ScanbotSdkExample.Droid.Utils;
using ScanbotSdkExample.Droid.Views;
using Android.Content.Res;
using Java.Lang;
using System;
using Exception = Java.Lang.Exception;
using _Microsoft.Android.Resource.Designer;
using Android.Util;
using Android.Media;
using Java.Nio;
using System.Text;
using System.Threading.Tasks;
using Math = System.Math;
using StringBuilder = System.Text.StringBuilder;

namespace ScanbotSdkExample.Droid.Activities
{
    /// <summary>
    /// Custom Android camera activity with native Camera2 API integration and Scanbot document detection.
    /// This activity provides a simple custom preview window showing real-time document detection with
    /// polygon overlay, user guidance, and document capture capability.
    /// </summary>
    [Activity(Theme = "@style/Theme.AppCompat")]
    public class CustomCameraPreviewActivity : AppCompatActivity, ISurfaceHolderCallback, SurfaceTexture.IOnFrameAvailableListener
    {
        private const int CameraPermissionRequestCode = 100;
        private const int StoragePermissionRequestCode = 101;
        private const float DetectionFps = 5f; // Process frames at 5 FPS to reduce CPU usage
        private const int MaxDetectionWidth = 1000; // Downsample to max 1000px width for faster detection
        
        private SurfaceView _surfaceView;
        private DocumentPolygonOverlayView _polygonOverlay;
        private TextView _userGuidanceTextView;
        private ProgressBar _imageProcessingProgress;
        private ImageButton _flashButton;
        private ImageButton _autoSnappingToggleButton;
        private ImageButton _shutterButton;
        
        private CameraManager _cameraManager;
        private CameraCaptureSession _cameraCaptureSession;
        private CameraDevice _cameraDevice;
        private Surface _previewSurface;
        private SurfaceTexture _surfaceTexture;
            private string _cameraId;
        
        private IO.Scanbot.Sdk.ScanbotSDK _scanbotSdk;
        private IDocumentScanner           _documentScanner;
        private DocumentDetectionHandler  _detectionHandler;

        public enum PreferredCameraMode { Auto, Wide, Normal }
        // Default mode: Auto (existing heuristic). Set to Wide or Normal for direct comparison.
        internal PreferredCameraMode _preferredCameraMode = PreferredCameraMode.Normal;
        
        internal bool _isPhotoCapturing;
        internal bool _isCameraInitialized;
        internal bool _isScannerValid; // Flag to prevent using disposed scanner
        private bool _flashEnabled;
        private bool _autoSnappingEnabled = true;
        internal long _lastFrameProcessingTime;
        internal long _frameProcessingIntervalMs;
        
        // Cache for array conversions to reduce allocations in hot detection path
        private PointF[] _detectionPointsCache = new PointF[4];
        
        // Digital zoom-out factor for normal camera: 0.65 = shows ~1.5x larger area
        private const float DigitalZoomOutFactor = 0.65f;
        
        private HandlerThread _backgroundThread;
        private Handler _backgroundHandler;
        // Dedicated thread/handler for heavy image processing (decode + detection)
        private HandlerThread _processingThread;
        public Handler _processingHandler;
        internal bool _awaitingStill;
        
        // For frame capture
        private int _previewWidth;
        private int _previewHeight;
        // Preview (YUV) reader for real-time detection
        private ImageReader _imageReaderPreview;
        private Surface _imageReaderPreviewSurface;
        // Still (JPEG) reader for high-quality capture
        private ImageReader _imageReaderStill;
        private Surface _imageReaderStillSurface;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            Log.Info("CustomCamera", "▶ OnCreate START");
            SupportRequestWindowFeature(WindowCompat.FeatureActionBarOverlay);
            base.OnCreate(savedInstanceState);
            
            SetContentView(Resource.Layout.custom_camera_preview);
            Log.Debug("CustomCamera", "✓ Layout inflated");
            
            // Initialize SDK and camera components
            try
            {
                _scanbotSdk = new IO.Scanbot.Sdk.ScanbotSDK(this);
                _documentScanner = _scanbotSdk.CreateDocumentScanner();
                _isScannerValid = true; // Mark scanner as valid
                _frameProcessingIntervalMs = (long)(1000 / DetectionFps);
                Log.Debug("CustomCamera", "✓ Scanbot SDK initialized");
            }
            catch (Exception ex)
            {
                Log.Error("CustomCamera", $"✗ Failed to initialize Scanbot SDK: {ex.Message}\n{ex.StackTrace}");
                ShowMessage("Failed to initialize scanner");
                Finish();
                return;
            }
            
            // Initialize UI elements FIRST
            try
            {
                InitializeUIElements();
                Log.Debug("CustomCamera", "✓ UI elements initialized");
                // Allow runtime override of camera selection for testing: set Intent extra "preferred_camera_mode" to "wide" or "normal"
                try
                {
                    var modeExtra = Intent?.GetStringExtra("preferred_camera_mode");
                    if (!string.IsNullOrEmpty(modeExtra))
                    {
                        if (modeExtra.Equals("wide", StringComparison.OrdinalIgnoreCase))
                            _preferredCameraMode = PreferredCameraMode.Wide;
                        else if (modeExtra.Equals("normal", StringComparison.OrdinalIgnoreCase))
                            _preferredCameraMode = PreferredCameraMode.Normal;
                        else
                            _preferredCameraMode = PreferredCameraMode.Auto;

                        Log.Info("CustomCamera", $"PreferredCameraMode override: {_preferredCameraMode}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("CustomCamera", $"Failed to read preferred camera mode: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log.Error("CustomCamera", $"✗ Failed to initialize UI: {ex.Message}\n{ex.StackTrace}");
                ShowMessage("Failed to initialize UI");
                Finish();
                return;
            }
            
            // Request camera and storage permissions AFTER UI is ready
            Log.Debug("CustomCamera", $"Checking permissions on API level {Build.VERSION.SdkInt}");
            if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
            {
                var requiredPermissions = new List<string>();
                
                // Camera permission is REQUIRED for the app to function
                if (CheckSelfPermission(Android.Manifest.Permission.Camera) != Android.Content.PM.Permission.Granted)
                    requiredPermissions.Add(Android.Manifest.Permission.Camera);
                
                // NOTE: MANAGE_EXTERNAL_STORAGE on Android 11+ is optional
                // File saving will gracefully fail if permission is not granted
                // We don't request it here to avoid permission dialog issues
                
                if (requiredPermissions.Count > 0)
                {
                    Log.Info("CustomCamera", $"📋 Requesting {requiredPermissions.Count} permissions: {string.Join(", ", requiredPermissions)}");
                    RequestPermissions(requiredPermissions.ToArray(), CameraPermissionRequestCode);
                }
                else
                {
                    Log.Info("CustomCamera", "✓ All required permissions already granted");
                    InitializeCamera();
                }
            }
            else
            {
                Log.Info("CustomCamera", "Pre-M device, initializing camera directly");
                InitializeCamera();
            }
            
            SupportActionBar?.Hide();
            Log.Info("CustomCamera", "▶ OnCreate END");
        }

        private void InitializeUIElements()
        {
            _surfaceView = FindViewById<SurfaceView>(Resource.Id.texture_view);
            if (_surfaceView == null)
                throw new Exception("SurfaceView not found in layout (id: texture_view)");
            Android.Util.Log.Debug("CustomCamera", $"✓ SurfaceView found");
            _surfaceView.Holder.AddCallback(this);
            
            _polygonOverlay = FindViewById<DocumentPolygonOverlayView>(Resource.Id.polygon_overlay);
            if (_polygonOverlay == null)
                throw new Exception("DocumentPolygonOverlayView not found in layout (id: polygon_overlay)");
            Android.Util.Log.Debug("CustomCamera", $"✓ Polygon overlay found");
            _polygonOverlay.Visibility = ViewStates.Visible;
            Android.Util.Log.Info("CustomCamera", "✓ Overlay is now VISIBLE to display detection results");
            
            _userGuidanceTextView = FindViewById<TextView>(Resource.Id.user_guidance_text_view);
            if (_userGuidanceTextView == null)
                throw new Exception("TextView not found in layout (id: user_guidance_text_view)");
                
            _imageProcessingProgress = FindViewById<ProgressBar>(Resource.Id.image_processing_progress);
            if (_imageProcessingProgress == null)
                throw new Exception("ProgressBar not found in layout (id: image_processing_progress)");
            
            _flashButton = FindViewById<ImageButton>(Resource.Id.flash_button);
            if (_flashButton == null)
                throw new Exception("Flash ImageButton not found in layout (id: flash_button)");
            _flashButton.Click += (s, e) => { ToggleFlash(); UpdateFlashIcon(); };

            _autoSnappingToggleButton = FindViewById<ImageButton>(Resource.Id.auto_snapping_toggle_button);
            if (_autoSnappingToggleButton == null)
                throw new Exception("Auto snapping ImageButton not found in layout (id: auto_snapping_toggle_button)");
            _autoSnappingToggleButton.Click += (s, e) => { ToggleAutoSnapping(); UpdateAutoSnapIcon(); };

            // Initialize icons state
            UpdateFlashIcon();
            UpdateAutoSnapIcon();
            
            _shutterButton = FindViewById<ImageButton>(Resource.Id.shutter_button);
            if (_shutterButton == null)
                throw new Exception("ImageButton not found in layout (id: shutter_button)");
            _shutterButton.Click += (s, e) => CaptureDocument();
            
            // Create detection handler for real-time processing
            try
            {
                _detectionHandler = new DocumentDetectionHandler(
                    _polygonOverlay,
                    _userGuidanceTextView,
                    ShowUserGuidance
                );
                Log.Info("CustomCamera", "✓ Detection handler created");
            }
            catch (Exception ex)
            {
                Log.Error("CustomCamera", $"Failed to create detection handler: {ex.Message}");
                throw;
            }
        }

        private void InitializeCamera()
        {
            try
            {
                _cameraManager = (CameraManager)GetSystemService(Context.CameraService);
                if (_cameraManager == null)
                {
                    Log.Error("CustomCamera", "✗ Failed to get CameraManager service");
                    ShowMessage("Failed to initialize camera");
                    Finish();
                    return;
                }
                
                Log.Info("CustomCamera", "✓ CameraManager initialized");
                StartBackgroundThread();
            }
            catch (Exception ex)
            {
                Log.Error("CustomCamera", $"✗ InitializeCamera error: {ex.Message}");
                ShowMessage("Failed to initialize camera");
                Finish();
            }
        }

        private void StartBackgroundThread()
        {
            _backgroundThread = new HandlerThread("CameraBackground");
            _backgroundThread.Start();
            _backgroundHandler = new Handler(_backgroundThread.Looper);
            // Start processing thread used for heavy work (bitmap decode, detection)
            try
            {
                _processingThread = new HandlerThread("ImageProcessing");
                _processingThread.Start();
                _processingHandler = new Handler(_processingThread.Looper);
            }
            catch (Exception ex)
            {
                Log.Warn("CustomCamera", $"Failed to start processing thread: {ex.Message}");
                _processingThread = null;
                _processingHandler = null;
            }
        }

        private void StopBackgroundThread()
        {
            // Request quit safely on both threads and clear handlers immediately to avoid UI blocking
            try
            {
                var bgThread = _backgroundThread;
                var procThread = _processingThread;

                if (bgThread != null)
                {
                    try { bgThread.QuitSafely(); }
                    catch (Exception ex) { Log.Warn("CustomCamera", $"backgroundThread.QuitSafely() failed: {ex.Message}"); }
                    _backgroundThread = null;
                    _backgroundHandler = null;

                    // Join in background to avoid blocking UI
                    Task.Run(() =>
                    {
                        try
                        {
                            long t0 = JavaSystem.CurrentTimeMillis();
                            bgThread.Join();
                            long t1 = JavaSystem.CurrentTimeMillis();
                            Log.Debug("CustomCamera", $"Background thread joined after {t1 - t0} ms");
                        }
                        catch (Exception ex)
                        {
                            Log.Error("CustomCamera", $"Error joining background thread: {ex.Message}");
                        }
                    });
                }

                if (procThread != null)
                {
                    try { procThread.QuitSafely(); }
                    catch (Exception ex) { Log.Warn("CustomCamera", $"processingThread.QuitSafely() failed: {ex.Message}"); }
                    _processingThread = null;
                    _processingHandler = null;

                    // Join in background to avoid blocking UI
                    Task.Run(() =>
                    {
                        try
                        {
                            long t0 = JavaSystem.CurrentTimeMillis();
                            procThread.Join();
                            long t1 = JavaSystem.CurrentTimeMillis();
                            Log.Debug("CustomCamera", $"Processing thread joined after {t1 - t0} ms");
                        }
                        catch (Exception ex)
                        {
                            Log.Error("CustomCamera", $"Error joining processing thread: {ex.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error("CustomCamera", $"StopBackgroundThread error: {ex.Message}");
            }
        }

        public void SurfaceChanged(ISurfaceHolder holder, Android.Graphics.Format format, int width, int height)
        {
            Log.Debug("CustomCamera", $"SurfaceChanged: {width}x{height}, format: {format}");
            
            _previewWidth = width;
            _previewHeight = height;
            
            // Create the Surface for camera preview
            _previewSurface = holder.Surface;
            Log.Debug("CustomCamera", $"Preview surface created: {_previewSurface}");
            
            // SOLUTION 3: Adjust SurfaceView dimensions to match preview aspect ratio
            // This ensures the preview display shows the same aspect ratio as the camera produces
            try
            {
                // Get the parent container to calculate available space
                ViewGroup parent = (ViewGroup)_surfaceView.Parent;
                if (parent != null)
                {
                    int parentWidth = parent.Width;
                    int parentHeight = parent.Height;
                    
                    // Target 4:3 aspect ratio
                    const double TARGET_ASPECT = 4.0 / 3.0; // 1.333
                    
                    // Calculate new dimensions to fit the parent while maintaining 4:3 aspect
                    double newWidth, newHeight;
                    
                    // Check if we should constrain by width or height
                    double parentAspect = (double)parentWidth / parentHeight;
                    
                    if (parentAspect > TARGET_ASPECT)
                    {
                        // Parent is wider than target aspect - constrain by height
                        newHeight = parentHeight;
                        newWidth = parentHeight * TARGET_ASPECT;
                    }
                    else
                    {
                        // Parent is taller than target aspect - constrain by width
                        newWidth = parentWidth;
                        newHeight = parentWidth / TARGET_ASPECT;
                    }
                    
                    // Update SurfaceView layout parameters to new dimensions
                    var layoutParams = _surfaceView.LayoutParameters as ViewGroup.LayoutParams;
                    if (layoutParams != null)
                    {
                        layoutParams.Width = (int)newWidth;
                        layoutParams.Height = (int)newHeight;
                        
                        // Center the view in parent if using FrameLayout
                        if (layoutParams is FrameLayout.LayoutParams frameParams)
                        {
                            frameParams.Gravity = GravityFlags.Center;
                        }
                        
                        _surfaceView.LayoutParameters = layoutParams;
                        Log.Info("CustomCamera", $"✓ SOLUTION 3: Adjusted SurfaceView to {(int)newWidth}x{(int)newHeight} " +
                                                 $"(aspect: {newWidth / newHeight:F3}) to match 4:3 preview");
                    }
                    
                    // CRITICAL FIX: Also resize the polygon overlay to match SurfaceView dimensions
                    // This ensures detection boundaries align with the actual camera preview area
                    if (_polygonOverlay != null)
                    {
                        var overlayParams = _polygonOverlay.LayoutParameters as ViewGroup.LayoutParams;
                        if (overlayParams != null)
                        {
                            overlayParams.Width = (int)newWidth;
                            overlayParams.Height = (int)newHeight;
                            
                            // Center the overlay to match SurfaceView position
                            if (overlayParams is FrameLayout.LayoutParams overlayFrameParams)
                            {
                                overlayFrameParams.Gravity = GravityFlags.Center;
                            }
                            
                            _polygonOverlay.LayoutParameters = overlayParams;
                            Log.Info("CustomCamera", $"✓ Adjusted polygon overlay to match SurfaceView: {(int)newWidth}x{(int)newHeight}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("CustomCamera", $"Failed to adjust SurfaceView dimensions: {ex.Message}");
            }
            
            // Open camera now that surface is ready
            OpenCamera();
        }

        public void SurfaceCreated(ISurfaceHolder holder)
        {
            Log.Debug("CustomCamera", "SurfaceCreated called");
            // Surface is created, ready for camera
        }

        public void SurfaceDestroyed(ISurfaceHolder holder)
        {
            Log.Debug("CustomCamera", "SurfaceDestroyed called");
            _previewSurface = null;
        }

        public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
        {
            // DEPRECATED: Using SurfaceView instead of TextureView
        }

        public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height)
        {
            // DEPRECATED: Using SurfaceView instead of TextureView
        }

        public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
        {
            // DEPRECATED: Using SurfaceView instead of TextureView
            return true;
        }

        public void OnSurfaceTextureUpdated(SurfaceTexture surface)
        {
            // DEPRECATED: Using SurfaceView instead of TextureView
        }

        public void OnFrameAvailable(SurfaceTexture surfaceTexture)
        {
            // Frame available callback from SurfaceTexture
            // Note: ImageReader will handle frame capture, not GetBitmap()
            Log.Verbose("CustomCamera", "OnFrameAvailable called from SurfaceTexture");
        }

        public void OnSurfaceTextureFrameAvailable(SurfaceTexture surface)
        {
            // DEPRECATED: Using ImageReader for frame capture instead
            // This method is no longer called
        }

        internal void ProcessFrameForDocumentDetection(Bitmap frameBitmap)
        {
            try
            {
                // Skip preview detection when still image is being processed to avoid queue saturation
                if (_awaitingStill)
                {
                    return;
                }
                
                // Check if scanner is still valid before using it
                if (!_isScannerValid)
                {
                    Log.Debug("CustomCamera", "⚠️ Scanner is disposed, skipping frame processing");
                    return;
                }
                
                if (_documentScanner == null)
                {
                    Log.Error("CustomCamera", "✗ DocumentScanner is null");
                    return;
                }
                
                if (frameBitmap == null)
                {
                    Log.Error("CustomCamera", "✗ Frame bitmap is null");
                    return;
                }

                var startTime = System.Diagnostics.Stopwatch.StartNew();
                
                // PERFORMANCE: Downsample large frames for faster detection
                // Detection doesn't need full resolution - 1000px width is sufficient
                Bitmap detectionBitmap = frameBitmap;
                bool needsDisposal = false;
                
                if (frameBitmap.Width > MaxDetectionWidth)
                {
                    float scale = (float)MaxDetectionWidth / frameBitmap.Width;
                    int newWidth = MaxDetectionWidth;
                    int newHeight = (int)(frameBitmap.Height * scale);
                    
                    detectionBitmap = Bitmap.CreateScaledBitmap(frameBitmap, newWidth, newHeight, false);
                    needsDisposal = true;
                    
                    Log.Debug("CustomCamera", $"Downsampled {frameBitmap.Width}x{frameBitmap.Height} -> {newWidth}x{newHeight} (scale={scale:F2})");
                }
                
                var downsampleTime = startTime.ElapsedMilliseconds;
                
                // Run document detection on the (possibly downsampled) frame
                var detectionResult = _documentScanner.ScanFromBitmap(detectionBitmap);
                var detectionTime = startTime.ElapsedMilliseconds - downsampleTime;
                
                // Dispose downsampled bitmap if created
                if (needsDisposal && detectionBitmap != null)
                {
                    detectionBitmap.Dispose();
                }
                
                // CRITICAL: Dispose original bitmap to prevent memory leak
                frameBitmap.Dispose();
                frameBitmap = null;
                
                startTime.Stop();
                Log.Info("CustomCamera", $"⏱️ Frame processing: downsample={downsampleTime}ms, detection={detectionTime}ms, total={startTime.ElapsedMilliseconds}ms");
                
                // Update UI with detection results
                RunOnUiThread(() =>
                {
                    if (detectionResult != null)
                    {
                        Log.Info("CustomCamera", $"✓ Detection status: {detectionResult.Status}, Points: {detectionResult.PointsNormalized.Count}");
                        
                        // Log point coordinates
                        if (detectionResult.PointsNormalized.Count > 0)
                        {
                            var points = detectionResult.PointsNormalized;
                            Log.Debug("CustomCamera", $"  Points: ({points[0].X:F3},{points[0].Y:F3}) ({points[1].X:F3},{points[1].Y:F3})");
                        }
                        
                        _detectionHandler.ShowDetectionStatus(detectionResult);
                        
                        // Auto-snap if detection is good and auto-snapping is enabled
                        if (_autoSnappingEnabled && 
                            detectionResult.Status == DocumentDetectionStatus.Ok &&
                            !_isPhotoCapturing)
                        {
                            Log.Info("CustomCamera", "✓ Auto-snapping: document detected with Ok status!");
                            CaptureDocument();
                        }
                    }
                    else
                    {
                        Log.Warn("CustomCamera", "✗ Detection returned null result");
                        _polygonOverlay.ClearPolygon();
                        _detectionHandler.ShowMessage("No document detected");
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error("CustomCamera", $"✗ Detection error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Selects the best available camera: widest (smallest focal length) for maximum document capture area.
        /// </summary>
        private string SelectBestCamera(string[] cameraIdList)
        {
            try
            {
                string bestCameraId = cameraIdList[0];
                float selectedFocal = _preferredCameraMode == PreferredCameraMode.Normal ? float.MinValue : float.MaxValue;

                // Iterate back-facing cameras and pick based on preferred mode
                foreach (var cameraId in cameraIdList)
                {
                    var characteristics = _cameraManager.GetCameraCharacteristics(cameraId);
                    var facing = (Integer)characteristics.Get(CameraCharacteristics.LensFacing);

                    if (facing?.IntValue() != (int)LensFacing.Back)
                        continue;

                    var focalLengths = (float[])characteristics.Get(CameraCharacteristics.LensInfoAvailableFocalLengths);
                    if (focalLengths == null || focalLengths.Length == 0)
                        continue;

                    // Use the camera's minimum focal length as representative (widest for that camera)
                    float minFocal = focalLengths[0];
                    foreach (var f in focalLengths)
                        if (f < minFocal) minFocal = f;

                    if (_preferredCameraMode == PreferredCameraMode.Wide)
                    {
                        // Pick camera with smallest focal length (widest)
                        if (minFocal < selectedFocal)
                        {
                            selectedFocal = minFocal;
                            bestCameraId = cameraId;
                        }
                    }
                    else if (_preferredCameraMode == PreferredCameraMode.Normal)
                    {
                        // Pick camera with largest (minFocal) focal length to get a 'normal' (narrower) view
                        if (minFocal > selectedFocal)
                        {
                            selectedFocal = minFocal;
                            bestCameraId = cameraId;
                        }
                    }
                    else // Auto: choose widest by default (existing behavior)
                    {
                        if (minFocal < selectedFocal)
                        {
                            selectedFocal = minFocal;
                            bestCameraId = cameraId;
                        }
                    }
                }

                Log.Info("CustomCamera", $"Selected camera {bestCameraId} with focal {selectedFocal}mm (mode: {_preferredCameraMode})");
                return bestCameraId;
            }
            catch (Exception ex)
            {
                Log.Error("CustomCamera", $"Error selecting camera: {ex.Message}");
                // Fallback to first camera on error
                return cameraIdList[0];
            }
        }

        private void OpenCamera()
        {
            try
            {
                if (_cameraManager == null)
                {
                    Log.Error("CustomCamera", "✗ CameraManager is null");
                    ShowMessage("Camera service not available");
                    return;
                }

                string[] cameraIdList = _cameraManager.GetCameraIdList();
                if (cameraIdList.Length == 0)
                {
                    ShowMessage("No camera found on device");
                    return;
                }

                // Try to find ultra-wide camera, fallback to standard back camera
                string cameraId = SelectBestCamera(cameraIdList);
                _cameraId = cameraId;
                
                if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
                {
                    if (CheckSelfPermission(Android.Manifest.Permission.Camera) != Android.Content.PM.Permission.Granted)
                    {
                        return;
                    }
                }

                _cameraManager.OpenCamera(cameraId, new CameraStateCallback(this), _backgroundHandler);
            }
            catch (CameraAccessException ex)
            {
                Log.Error("CustomCamera", $"Camera access error: {ex.Message}");
                ShowMessage("Cannot access camera");
            }
        }

        internal void OnCameraOpened(CameraDevice camera)
        {
            Log.Info("CustomCamera", "Camera opened successfully");
            _cameraDevice = camera;
            _isCameraInitialized = true;
            CreateCaptureSession();
        }

        internal void OnCameraDisconnected(CameraDevice camera)
        {
            Log.Warn("CustomCamera", "Camera disconnected");
            _isCameraInitialized = false;
        }

        internal void OnCameraError(CameraDevice camera, CameraError error)
        {
            Log.Error("CustomCamera", $"Camera error: {error}");
            _isCameraInitialized = false;
            ShowMessage("Camera error occurred");
        }

        private void CreateCaptureSession()
        {
            try
            {
                if (_cameraDevice == null || _previewSurface == null)
                {
                    Log.Error("CustomCamera", "Camera device or preview surface is null");
                    return;
                }

                Log.Debug("CustomCamera", "Creating capture session...");
                Log.Debug("CustomCamera", $"Preview surface: {_previewSurface}");
                Log.Debug("CustomCamera", $"Preview size from layout: {_previewWidth}x{_previewHeight}");
                
                // SOLUTION 1: Force preview to 4:3 aspect ratio to match capture
                // Find best 4:3 preview size from available options
                try
                {
                    var characteristics = _cameraManager.GetCameraCharacteristics(_cameraId);
                    var configMapObj = characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
                    
                    if (configMapObj is Android.Hardware.Camera2.Params.StreamConfigurationMap scm)
                    {
                        var previewSizes = scm.GetOutputSizes(Java.Lang.Class.FromType(typeof(SurfaceTexture)));
                        
                        Log.Debug("CustomCamera", $"Available preview sizes: {previewSizes.Length}");
                        var jpegSizesList = new StringBuilder();
                        foreach (var s in previewSizes)
                        {
                            float aspect = (float)s.Width / s.Height;
                            jpegSizesList.Append($"{s.Width}x{s.Height}({aspect:F2}) ");
                        }
                        Log.Info("CustomCamera", $"Available preview aspect ratios: {jpegSizesList}");
                        
                        // Filter for 4:3 aspect ratio
                        const double TARGET_ASPECT = 4.0 / 3.0;
                        const double ASPECT_TOLERANCE = 0.01;
                        
                        Android.Util.Size bestPreviewSize = null;
                        long maxPixels = 0;
                        
                        foreach (var s in previewSizes)
                        {
                            float aspect = (float)s.Width / s.Height;
                            double aspectDiff = Math.Abs(aspect - TARGET_ASPECT);
                            
                            if (aspectDiff <= ASPECT_TOLERANCE)
                            {
                                long pixels = (long)s.Width * s.Height;
                                if (pixels > maxPixels)
                                {
                                    maxPixels = pixels;
                                    bestPreviewSize = s;
                                }
                            }
                        }
                        
                        if (bestPreviewSize != null)
                        {
                            _previewWidth = bestPreviewSize.Width;
                            _previewHeight = bestPreviewSize.Height;
                            float selectedAspect = (float)_previewWidth / _previewHeight;
                            Log.Info("CustomCamera", $"✓ Selected 4:3 preview size: {_previewWidth}x{_previewHeight} (aspect: {selectedAspect:F3})");
                        }
                        else
                        {
                            Log.Warn("CustomCamera", "No 4:3 preview size found, using layout dimensions: " + _previewWidth + "x" + _previewHeight);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("CustomCamera", $"Failed to select 4:3 preview size: {ex.Message}");
                }
                
                // Create ImageReader for getting raw camera frames (preview-sized YUV)
                _imageReaderPreview = ImageReader.NewInstance(_previewWidth, _previewHeight, ImageFormatType.Yuv420888, 2);
                _imageReaderPreviewSurface = _imageReaderPreview.Surface;
                _imageReaderPreview.SetOnImageAvailableListener(new PreviewImageAvailableListener(this), _backgroundHandler);
                Log.Debug("CustomCamera", $"Created preview ImageReader: {_previewWidth}x{_previewHeight}");
                
                // CRITICAL: Notify overlay of actual frame dimensions for correct coordinate scaling
                Log.Debug("CustomCamera", $"Before SetPreviewFrameDimensions: overlay will receive {_previewWidth}x{_previewHeight}");
                _polygonOverlay.SetPreviewFrameDimensions(_previewWidth, _previewHeight);
                Log.Debug("CustomCamera", $"✓ SetPreviewFrameDimensions called with {_previewWidth}x{_previewHeight}");

                // Create ImageReader for high-quality still capture
                // CRITICAL: Match the preview aspect ratio to avoid template mismatch!
                try
                {
                    var characteristics = _cameraManager.GetCameraCharacteristics(_cameraId);
                    var configMapObj = characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
                    Android.Util.Size[] configMap = null;
                    if (configMapObj is Android.Hardware.Camera2.Params.StreamConfigurationMap scm)
                    {
                        configMap = scm.GetOutputSizes((int)ImageFormatType.Jpeg);
                    }

                    // SOLUTION 2: Force 4:3 aspect ratio matching hardware JPEG sizes
                    // This maximizes resolution and uses native hardware capabilities
                    float previewAspect = (float)_previewWidth / _previewHeight;
                    Log.Debug("CustomCamera", $"Preview aspect ratio: {previewAspect:F3} ({_previewWidth}x{_previewHeight})");
                    
                    // Log all available JPEG sizes for debugging
                    if (configMap != null && configMap.Length > 0)
                    {
                        var jpegSizesList = new StringBuilder();
                        jpegSizesList.Append("Available JPEG sizes: ");
                        for (int i = 0; i < configMap.Length; i++)
                        {
                            var s = configMap[i];
                            float jpegAspect = (float)s.Width / s.Height;
                            jpegSizesList.Append($"{s.Width}x{s.Height}({jpegAspect:F2}) ");
                        }
                        Log.Info("CustomCamera", jpegSizesList.ToString());
                    }
                    else
                    {
                        Log.Warn("CustomCamera", "No JPEG sizes available from camera!");
                    }
                    
                    // Filter for 4:3 aspect ratio (target = 1.333, tolerance 0.01 = ~1%)
                    const double TARGET_ASPECT = 4.0 / 3.0; // 1.333
                    const double ASPECT_TOLERANCE = 0.01;
                    
                    Android.Util.Size bestMatch = null;
                    long maxPixels = 0;
                    
                    if (configMap != null && configMap.Length > 0)
                    {
                        var matching4to3Sizes = new List<Android.Util.Size>();
                        
                        foreach (var s in configMap)
                        {
                            float jpegAspect = (float)s.Width / s.Height;
                            double aspectDiff = Math.Abs(jpegAspect - TARGET_ASPECT);
                            
                            // Check if within tolerance of 4:3
                            if (aspectDiff <= ASPECT_TOLERANCE)
                            {
                                matching4to3Sizes.Add(s);
                                long pixels = (long)s.Width * s.Height;
                                if (pixels > maxPixels)
                                {
                                    maxPixels = pixels;
                                    bestMatch = s;
                                }
                            }
                        }
                        
                        if (matching4to3Sizes.Count > 0)
                        {
                            Log.Info("CustomCamera", $"Found {matching4to3Sizes.Count} JPEG sizes with 4:3 aspect ratio (within {ASPECT_TOLERANCE:F3} tolerance)");
                            foreach (var size in matching4to3Sizes)
                            {
                                float aspect = (float)size.Width / size.Height;
                                Log.Debug("CustomCamera", $"  ├─ {size.Width}x{size.Height} (aspect: {aspect:F3})");
                            }
                        }
                    }
                    
                    // Fallback: if no 4:3 match, use largest available
                    if (bestMatch == null && configMap != null && configMap.Length > 0)
                    {
                        Log.Warn("CustomCamera", "No 4:3 JPEG size found within tolerance, selecting largest available");
                        maxPixels = 0;
                        foreach (var s in configMap)
                        {
                            long pixels = (long)s.Width * s.Height;
                            if (pixels > maxPixels)
                            {
                                maxPixels = pixels;
                                bestMatch = s;
                            }
                        }
                    }
                    
                    // Last resort: use preview size
                    if (bestMatch == null)
                    {
                        Log.Warn("CustomCamera", "No JPEG sizes available, using preview size as fallback");
                        bestMatch = new Android.Util.Size(_previewWidth, _previewHeight);
                    }
                    
                    float selectedAspect = (float)bestMatch.Width / bestMatch.Height;
                    Log.Info("CustomCamera", $"Selected JPEG size: {bestMatch.Width}x{bestMatch.Height} (aspect: {selectedAspect:F3}, target was 4:3 = {TARGET_ASPECT:F3})");
                    
                    _imageReaderStill = ImageReader.NewInstance(bestMatch.Width, bestMatch.Height, ImageFormatType.Jpeg, 2);
                    _imageReaderStillSurface = _imageReaderStill.Surface;
                    _imageReaderStill.SetOnImageAvailableListener(new StillImageAvailableListener(this), _backgroundHandler);
                    Log.Debug("CustomCamera", $"Created still ImageReader: {bestMatch.Width}x{bestMatch.Height}");
                }
                catch (Exception ex)
                {
                    Log.Warn("CustomCamera", $"Failed to create still ImageReader with 4:3 aspect matching: {ex.Message}");
                    _imageReaderStill = ImageReader.NewInstance(_previewWidth, _previewHeight, ImageFormatType.Jpeg, 2);
                    _imageReaderStillSurface = _imageReaderStill.Surface;
                    _imageReaderStill.SetOnImageAvailableListener(new StillImageAvailableListener(this), _backgroundHandler);
                    Log.Debug("CustomCamera", $"Fallback to preview size ImageReader: {_previewWidth}x{_previewHeight}");
                }
                
                var previewRequestBuilder = _cameraDevice.CreateCaptureRequest(CameraTemplate.Preview);
                Log.Debug("CustomCamera", "Adding preview surface to capture request");
                previewRequestBuilder.AddTarget(_previewSurface);
                Log.Debug("CustomCamera", "Added preview surface - camera will render here");
                
                // Also add ImageReader surface for frame capture
                // Add preview image reader (YUV) for frame processing
                previewRequestBuilder.AddTarget(_imageReaderPreviewSurface);
                Log.Debug("CustomCamera", "Added preview ImageReader surface for frame capture");

                // Note: still ImageReader (JPEG) is not added to preview request. It will be targeted during still-capture request.

                // Force full sensor field of view - explicitly disable any default cropping
                try
                {
                    var characteristics = _cameraManager.GetCameraCharacteristics(_cameraId);
                    var sensorRectObj = characteristics.Get(CameraCharacteristics.SensorInfoActiveArraySize);
                    if (sensorRectObj is Android.Graphics.Rect sensorRect)
                    {
                        previewRequestBuilder.Set(CaptureRequest.ScalerCropRegion, sensorRect);
                        Log.Debug("CustomCamera", $"Preview: Set crop region to full sensor {sensorRect.Width()}x{sensorRect.Height()}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("CustomCamera", $"Could not set full-sensor crop region for preview: {ex.Message}");
                }

                // Set auto-focus mode
                previewRequestBuilder.Set(CaptureRequest.ControlAfMode, (int)ControlAFMode.ContinuousVideo);
                Log.Debug("CustomCamera", "Set auto-focus mode");
                
                // Set auto-exposure mode
                previewRequestBuilder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.On);
                Log.Debug("CustomCamera", "Set auto-exposure mode");

                // Focus Distance Locking: Optimize for close-range macro capture (e.g., 11" distance)
                try
                {
                    var characteristics = _cameraManager.GetCameraCharacteristics(_cameraId);
                    
                    // Check if focus distance is available
                    var focusDistanceObj = characteristics.Get(CameraCharacteristics.LensInfoMinimumFocusDistance);
                    if (focusDistanceObj != null)
                    {
                        float minFocusDistance = Convert.ToSingle(focusDistanceObj);
                        Log.Debug("CustomCamera", $"Camera minimum focus distance: {minFocusDistance} diopters (1/{minFocusDistance:F2} = ~{(minFocusDistance > 0 ? (100 / minFocusDistance).ToString("F1") : "∞")}cm)");
                        
                        // For macro-capable devices (minFocus < 20 diopters / ~5cm), lock focus distance
                        // at a point optimal for close-range document capture at ~11" (27.9cm) distance
                        if (minFocusDistance > 0 && minFocusDistance < 20)
                        {
                            // Calculate optimal focus lock: set just slightly above minimum to avoid hunting
                            // For 11" (27.9cm) distance, aim for ~0.10-0.15 diopters (6-10cm hyperfocal distance)
                            float targetFocusDistance = Math.Max(0.05f, minFocusDistance + 0.02f);
                            
                            // Clamp to valid range
                            targetFocusDistance = Math.Min(targetFocusDistance, 5.0f);
                            
                            previewRequestBuilder.Set(CaptureRequest.LensFocusDistance, targetFocusDistance);
                            Log.Info("CustomCamera", $"✓ Focus distance locked to {targetFocusDistance:F3} diopters (~{(targetFocusDistance > 0 ? (100 / targetFocusDistance).ToString("F1") : "∞")}cm) for close-range macro optimization");
                        }
                        else if (minFocusDistance >= 20)
                        {
                            Log.Debug("CustomCamera", "Camera has limited macro capability - continuous auto-focus recommended");
                        }
                    }
                    else
                    {
                        Log.Debug("CustomCamera", "Minimum focus distance not available on this device - using continuous auto-focus");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("CustomCamera", $"Failed to apply focus distance locking: {ex.Message}");
                }

                // No additional crop region - using normal camera native field of view

                _cameraDevice.CreateCaptureSession(
                    new[] { _previewSurface, _imageReaderPreviewSurface, _imageReaderStillSurface },
                    new CaptureSessionCallback(this, previewRequestBuilder),
                    _backgroundHandler
                );
            }
            catch (CameraAccessException ex)
            {
                Log.Error("CustomCamera", $"Capture session error: {ex.Message}");
            }
        }

        internal void OnCaptureSessionConfigured(CaptureRequest previewRequest)
        {
            if (_cameraCaptureSession == null)
            {
                Log.Error("CustomCamera", "Capture session is null");
                return;
            }

            try
            {
                Log.Debug("CustomCamera", "Setting repeating request for preview");
                Log.Debug("CustomCamera", $"Preview request: {previewRequest}");
                _cameraCaptureSession.SetRepeatingRequest(previewRequest, null, null);
                Log.Debug("CustomCamera", "Repeating request set successfully");
            }
            catch (CameraAccessException ex)
            {
                Log.Error("CustomCamera", $"Capture session config error: {ex.Message}");
            }
        }

        internal void OnCaptureSessionCreated(CameraCaptureSession session)
        {
            Log.Info("CustomCamera", "Capture session created");
            _cameraCaptureSession = session;
        }

        private void ToggleFlash()
        {
            if (_cameraDevice == null || _cameraCaptureSession == null)
            {
                return;
            }

            try
            {
                // Toggle desired state
                _flashEnabled = !_flashEnabled;

                // Check if camera supports flash
                bool flashAvailable = false;
                try
                {
                    if (_cameraManager != null && !string.IsNullOrEmpty(_cameraId))
                    {
                        var chars = _cameraManager.GetCameraCharacteristics(_cameraId);
                        var flashInfo = chars.Get(CameraCharacteristics.FlashInfoAvailable);
                        if (flashInfo != null)
                        {
                            try
                            {
                                // Some bindings return Java.Lang.Boolean, sometimes a raw bool
                                var jb = flashInfo as Java.Lang.Boolean;
                                if (jb != null)
                                {
                                    flashAvailable = jb.BooleanValue();
                                }
                                else
                                {
                                    // attempt to convert via ToString
                                    var s = flashInfo.ToString();
                                    if (!string.IsNullOrEmpty(s) && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("false", StringComparison.OrdinalIgnoreCase)))
                                    {
                                        flashAvailable = s.Equals("true", StringComparison.OrdinalIgnoreCase);
                                    }
                                }
                            }
                            catch (Exception) { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("CustomCamera", $"Failed to query flash availability: {ex.Message}");
                }

                if (!flashAvailable)
                {
                    Log.Warn("CustomCamera", "Device reports no flash available");
                    RunOnUiThread(() => ShowMessage("Flash not available on this device"));
                    // ensure icon reflects that flash is off
                    _flashEnabled = false;
                    UpdateFlashIcon();
                    return;
                }

                // Prefer CameraManager.SetTorchMode for continuous torch control
                bool torchSet = false;
                try
                {
                    if (_cameraManager != null && !string.IsNullOrEmpty(_cameraId) && Build.VERSION.SdkInt >= BuildVersionCodes.M)
                    {
                        _cameraManager.SetTorchMode(_cameraId, _flashEnabled);
                        torchSet = true;
                        Log.Debug("CustomCamera", $"SetTorchMode({_cameraId},{_flashEnabled}) succeeded");
                    }
                }
                catch (SecurityException sex)
                {
                    Log.Warn("CustomCamera", $"SetTorchMode security exception: {sex.Message}");
                }
                catch (Exception ex)
                {
                    Log.Warn("CustomCamera", $"SetTorchMode failed: {ex.Message}");
                }

                if (!torchSet)
                {
                    // Fallback: set flash mode on preview capture request to TORCH (value 2) or OFF (0)
                    try
                    {
                        var requestBuilder = _cameraDevice.CreateCaptureRequest(CameraTemplate.Preview);
                        requestBuilder.AddTarget(_previewSurface);
                        // FLASH_MODE_TORCH = 2
                        requestBuilder.Set(CaptureRequest.FlashMode, new Java.Lang.Integer(_flashEnabled ? 2 : 0));
                        _cameraCaptureSession.SetRepeatingRequest(requestBuilder.Build(), null, null);
                        Log.Debug("CustomCamera", $"Fallback flash mode set to {(_flashEnabled ? 2 : 0)}");
                    }
                    catch (CameraAccessException ex)
                    {
                        Log.Error("CustomCamera", $"Fallback flash set failed: {ex.Message}");
                    }
                }

                UpdateFlashIcon();
            }
            catch (CameraAccessException ex)
            {
                Log.Error("CustomCamera", $"Flash toggle error: {ex.Message}");
            }
        }

        private void ToggleAutoSnapping()
        {
            _autoSnappingEnabled = !_autoSnappingEnabled;
            UpdateAutoSnapIcon();
            
            if (!_autoSnappingEnabled)
            {
                _userGuidanceTextView.Text = "";
                _polygonOverlay.ClearPolygon();
            }
        }

        private void UpdateFlashIcon()
        {
            try
            {
                if (_flashButton == null) return;
                // Dim icon when off
                _flashButton.Alpha = _flashEnabled ? 1.0f : 0.5f;
                _flashButton.ContentDescription = _flashEnabled ? "Flash on" : "Flash off";
            }
            catch (Exception ex)
            {
                Log.Warn("CustomCamera", $"UpdateFlashIcon failed: {ex.Message}");
            }
        }

        private void UpdateAutoSnapIcon()
        {
            try
            {
                if (_autoSnappingToggleButton == null) return;
                _autoSnappingToggleButton.Alpha = _autoSnappingEnabled ? 1.0f : 0.5f;
                _autoSnappingToggleButton.ContentDescription = _autoSnappingEnabled ? "Auto snapping on" : "Auto snapping off";
            }
            catch (Exception ex)
            {
                Log.Warn("CustomCamera", $"UpdateAutoSnapIcon failed: {ex.Message}");
            }
        }

        private void CaptureDocument()
        {
            if (_isPhotoCapturing || !_isCameraInitialized)
            {
                Log.Warn("CustomCamera", $"Capture skipped: PhotoCapturing={_isPhotoCapturing}, CameraInitialized={_isCameraInitialized}");
                return;
            }

            Log.Info("CustomCamera", "📸 Starting document capture...");
            _isPhotoCapturing = true;
            
            // Stop preview detection and live camera feed after shot is taken
            _autoSnappingEnabled = false;
            Log.Debug("CustomCamera", "⏸️ Auto-snapping disabled after capture");
            
            // Stop the repeating preview request to freeze the camera
            try
            {
                if (_cameraCaptureSession != null)
                {
                    _cameraCaptureSession.StopRepeating();
                    Log.Debug("CustomCamera", "⏸️ Camera preview stopped");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("CustomCamera", $"Failed to stop repeating request: {ex.Message}");
            }
            
            // Show progress
            RunOnUiThread(() =>
            {
                _imageProcessingProgress.Visibility = ViewStates.Visible;
                ShowMessage("Capturing document...");
            });

            // Trigger a high-quality still capture request
            // Compute rotation and orientation on UI thread to avoid calling WindowManager from background
            int jpegOrientation = 0;
            RunOnUiThread(() =>
            {
                try
                {
                    var rotation = WindowManager.DefaultDisplay.Rotation;
                    jpegOrientation = GetOrientation((int)rotation);
                }
                catch (Exception ex)
                {
                    Log.Warn("CustomCamera", $"Failed to get display rotation on UI thread: {ex.Message}");
                    jpegOrientation = 0;
                }
            });

            if (_backgroundHandler == null)
            {
                Log.Error("CustomCamera", "Background handler is null - cannot capture");
                _isPhotoCapturing = false;
                RunOnUiThread(() =>
                {
                    _imageProcessingProgress.Visibility = ViewStates.Gone;
                    ShowMessage("Internal error");
                });
                return;
            }

            _backgroundHandler.Post(() =>
            {
                try
                {
                    if (_cameraDevice == null)
                    {
                        Log.Error("CustomCamera", "Camera device is null at capture time");
                        throw new Exception("Camera device not available");
                    }

                    if (_cameraCaptureSession == null)
                    {
                        Log.Error("CustomCamera", "Capture session is null at capture time");
                        throw new Exception("Capture session not available");
                    }

                    if (_imageReaderStillSurface == null || _previewSurface == null)
                    {
                        Log.Error("CustomCamera", "One of capture surfaces is null (still or preview)");
                        throw new Exception("Capture surfaces not ready");
                    }

                    Log.Info("CustomCamera", "📸 Capture triggered - issuing still capture request");

                    var captureBuilder = _cameraDevice.CreateCaptureRequest(CameraTemplate.StillCapture);
                    captureBuilder.AddTarget(_imageReaderStillSurface);
                    // Also add preview surface to maintain exposure/AF
                    captureBuilder.AddTarget(_previewSurface);

                    // Use highest quality JPEG (boxed as byte where camera framework expects a byte)
                    captureBuilder.Set(CaptureRequest.JpegQuality, new Java.Lang.Byte((sbyte)100));

                    // Use auto-focus and auto-exposure (boxed integers)
                    captureBuilder.Set(CaptureRequest.ControlAfMode, new Java.Lang.Integer((int)ControlAFMode.ContinuousPicture));
                    captureBuilder.Set(CaptureRequest.ControlAeMode, new Java.Lang.Integer((int)ControlAEMode.OnAutoFlash));

                    // Force full sensor field of view on still capture to match preview request
                    try
                    {
                        var characteristics = _cameraManager.GetCameraCharacteristics(_cameraId);
                        var sensorRectObj = characteristics.Get(CameraCharacteristics.SensorInfoActiveArraySize);
                        if (sensorRectObj is Android.Graphics.Rect sensorRect)
                        {
                            captureBuilder.Set(CaptureRequest.ScalerCropRegion, sensorRect);
                            Log.Debug("CustomCamera", $"Still Capture: Set crop region to full sensor {sensorRect.Width()}x{sensorRect.Height()} - matching preview");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("CustomCamera", $"Could not set full-sensor crop region for still capture: {ex.Message}");
                    }

                    // Apply same focus distance locking as preview for consistency
                    try
                    {
                        var characteristics = _cameraManager.GetCameraCharacteristics(_cameraId);
                        var focusDistanceObj = characteristics.Get(CameraCharacteristics.LensInfoMinimumFocusDistance);
                        if (focusDistanceObj != null)
                        {
                            float minFocusDistance = Convert.ToSingle(focusDistanceObj);
                            if (minFocusDistance > 0 && minFocusDistance < 20)
                            {
                                float targetFocusDistance = Math.Max(0.05f, minFocusDistance + 0.02f);
                                targetFocusDistance = Math.Min(targetFocusDistance, 5.0f);
                                captureBuilder.Set(CaptureRequest.LensFocusDistance, targetFocusDistance);
                                Log.Debug("CustomCamera", $"Still capture focus distance locked to {targetFocusDistance:F3} diopters");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("CustomCamera", $"Failed to apply focus distance to still capture: {ex.Message}");
                    }

                    // No additional crop region on still capture - use native field of view for consistency with preview

                    // Orientation must be boxed as Integer to avoid JNI type mismatch
                    captureBuilder.Set(CaptureRequest.JpegOrientation, new Java.Lang.Integer(jpegOrientation));

                    var req = captureBuilder.Build();
                    Log.Debug("CustomCamera", $"Sending capture request: {req}");
                    // mark that we're waiting for the still image to arrive
                    _awaitingStill = true;
                    _cameraCaptureSession.Capture(req, new CameraCaptureCallback(this), _backgroundHandler);
                }
                catch (Exception ex)
                {
                    Log.Error("CustomCamera", $"Capture error: {ex.Message}");
                    _isPhotoCapturing = false;
                    RunOnUiThread(() =>
                    {
                        _imageProcessingProgress.Visibility = ViewStates.Gone;
                        ShowMessage("Failed to capture");
                    });
                }
            });
        }

        private int GetOrientation(int rotation)
        {
            switch (rotation)
            {
                case 0: return 0;
                case 1: return 90;
                case 2: return 180;
                case 3: return 270;
                default: return 0;
            }
        }

        public void ProcessCapturedImage(Bitmap capturedBitmap)
        {
            try
            {
                // Check if scanner is still valid before using it
                if (!_isScannerValid)
                {
                    Log.Error("CustomCamera", "✗ Scanner is disposed, cannot process captured image");
                    RunOnUiThread(() =>
                    {
                        _imageProcessingProgress.Visibility = ViewStates.Gone;
                        _isPhotoCapturing = false;
                        ShowMessage("Scanner is no longer available");
                    });
                    return;
                }
                
                Log.Info("CustomCamera", $"🔍 Processing captured image: {capturedBitmap.Width}x{capturedBitmap.Height}");
                
                // Detect document in the captured image
                var detectionResult = _documentScanner.ScanFromBitmap(capturedBitmap);

                Log.Debug("CustomCamera", $"Detection result: {(detectionResult != null ? "Success" : "Null")}");
                if (detectionResult != null)
                {
                    Log.Debug("CustomCamera", $"  Status: {detectionResult.Status}, Points: {detectionResult.PointsNormalized.Count}");
                }

                // Create a new document in the Scanbot storage
                var document = _scanbotSdk.DocumentApi.CreateDocument(0);
                Log.Debug("CustomCamera", $"Document created: {document.Uuid}");
                
                document.AddPage(capturedBitmap);
                Log.Debug("CustomCamera", "Page added to document");

                // Add detected polygon to the page if detection was successful
                if (detectionResult != null)
                {
                    document.PageAtIndex(0).Polygon = detectionResult.PointsNormalized;
                    Log.Debug("CustomCamera", "Polygon added to page");
                }

                // Save the document as image file to /sdcard/IG/
                try
                {
                    if (CheckStoragePermission())
                    {
                        // Save using lower-level method which prefers highest-quality stills
                        SaveCapturedDocumentFromBitmap(capturedBitmap, null);
                    }
                    else
                    {
                        // Permission not granted - store bitmap and request permission
                        Log.Warn("CustomCamera", "⚠️ No write permission - requesting storage permission for disk save...");
                        _pendingCapturedBitmap = capturedBitmap;
                        RequestStoragePermission();
                        Log.Info("CustomCamera", "Document will be available in Scanbot storage. Requesting permission to save to disk...");
                    }
                }
                catch (Exception saveEx)
                {
                    Log.Error("CustomCamera", $"✗ Failed to save document to disk: {saveEx.Message}");
                }

                // Return the document UUID as result (navigate regardless of disk save status)
                RunOnUiThread(() =>
                {
                    _imageProcessingProgress.Visibility = ViewStates.Gone;
                    _isPhotoCapturing = false;
                    
                    Log.Info("CustomCamera", $"✓ Document processed successfully. Navigating to preview...");
                    
                    // Start PagePreviewActivity to show the captured document
                    var intent = PagePreviewActivity.CreateIntent(this, document.Uuid);
                    StartActivity(intent);
                    
                    Finish();
                });
            }
            catch (Exception ex)
            {
                Log.Error("CustomCamera", $"✗ Image processing error: {ex.Message}\n{ex.StackTrace}");
                _isPhotoCapturing = false;
                RunOnUiThread(() =>
                {
                    _imageProcessingProgress.Visibility = ViewStates.Gone;
                    ShowMessage("Failed to process image");
                });
            }
        }

        public void ProcessCapturedStill(byte[] jpegData, Bitmap capturedBitmap)
        {
            Log.Info("CustomCamera", $"[PROCESS-1] ▶ ProcessCapturedStill STARTED - jpegData={(jpegData != null ? jpegData.Length + " bytes" : "null")}, capturedBitmap={(capturedBitmap != null ? capturedBitmap.Width + "x" + capturedBitmap.Height : "null")}");
            
            try
            {
                if (!_isScannerValid)
                {
                    Log.Error("CustomCamera", "[PROCESS-2] ✗ Scanner is disposed, cannot process captured still");
                    RunOnUiThread(() =>
                    {
                        _imageProcessingProgress.Visibility = ViewStates.Gone;
                        _isPhotoCapturing = false;
                        ShowMessage("Scanner is no longer available");
                    });
                    return;
                }

                // Decode a small preview bitmap for fast detection (avoid decoding full-res unless necessary)
                Bitmap detectionBitmap = capturedBitmap;
                bool fullBitmapDecoded = capturedBitmap != null;

                if (detectionBitmap == null && jpegData != null)
                {
                    try
                    {
                        var opts = new Android.Graphics.BitmapFactory.Options();
                        opts.InJustDecodeBounds = true;
                        Android.Graphics.BitmapFactory.DecodeByteArray(jpegData, 0, jpegData.Length, opts);

                        // Target a smaller detection size (max dimension 1024)
                        int maxDim = Math.Max(opts.OutWidth, opts.OutHeight);
                        int sample = 1;
                        while (maxDim / sample > 1024) sample <<= 1;

                        opts.InJustDecodeBounds = false;
                        opts.InSampleSize = sample;
                        detectionBitmap = Android.Graphics.BitmapFactory.DecodeByteArray(jpegData, 0, jpegData.Length, opts);
                        fullBitmapDecoded = false;
                        Log.Debug("CustomCamera", $"Decoded detection bitmap with sample={opts.InSampleSize}: {detectionBitmap?.Width}x{detectionBitmap?.Height}");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("CustomCamera", $"Failed to decode scaled detection bitmap: {ex.Message}");
                        detectionBitmap = null;
                    }
                }

                if (detectionBitmap == null)
                {
                    Log.Error("CustomCamera", "✗ No bitmap available for detection");
                    RunOnUiThread(() =>
                    {
                        _imageProcessingProgress.Visibility = ViewStates.Gone;
                        _isPhotoCapturing = false;
                        ShowMessage("Failed to decode image");
                    });
                    return;
                }

                Log.Info("CustomCamera", $"🔍 Running detection on bitmap: {detectionBitmap.Width}x{detectionBitmap.Height}");

                long detectStart = JavaSystem.CurrentTimeMillis();
                DocumentDetectionResult detectionResult = null;
                try
                {
                    detectionResult = _documentScanner.ScanFromBitmap(detectionBitmap);
                }
                catch (Exception ex)
                {
                    Log.Error("CustomCamera", $"ScanFromBitmap threw: {ex.Message}");
                }
                long detectEnd = JavaSystem.CurrentTimeMillis();
                Log.Info("CustomCamera", $"Detection time: {detectEnd - detectStart} ms");
                
                // CRITICAL: Dispose downsampled detection bitmap if it was created (not the same as capturedBitmap)
                if (!fullBitmapDecoded && detectionBitmap != null && detectionBitmap != capturedBitmap)
                {
                    detectionBitmap.Dispose();
                    detectionBitmap = null;
                    Log.Debug("CustomCamera", "Disposed downsampled detection bitmap");
                }

                // If we only decoded a small detection bitmap, but need full-res for saving/document, decode full now
                Bitmap fullBitmap = null;
                if (!fullBitmapDecoded && jpegData != null)
                {
                    try
                    {
                        // If we have a detection result with polygon, decode only the bounding region to save memory
                        if (detectionResult != null && detectionResult.PointsNormalized != null && detectionResult.PointsNormalized.Count == 4)
                        {
                            fullBitmap = DecodeRegionForPolygon(jpegData, detectionResult.PointsNormalized);
                            if (fullBitmap != null)
                            {
                                Log.Info("CustomCamera", $"Region bitmap decoded for polygon: {fullBitmap.Width}x{fullBitmap.Height} (memory optimized)");
                            }
                        }
                        
                        // Fallback to full decode if region decode failed or no polygon
                        if (fullBitmap == null)
                        {
                            fullBitmap = Android.Graphics.BitmapFactory.DecodeByteArray(jpegData, 0, jpegData.Length);
                            Log.Debug("CustomCamera", $"Full bitmap decoded for saving: {fullBitmap?.Width}x{fullBitmap?.Height}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("CustomCamera", $"Failed to decode bitmap: {ex.Message}");
                    }
                }
                else
                {
                    fullBitmap = capturedBitmap;
                }

                // Create document (use fullBitmap if available, otherwise detectionBitmap)
                var pageBitmap = fullBitmap ?? detectionBitmap;

                var document = _scanbotSdk.DocumentApi.CreateDocument(0);
                document.AddPage(pageBitmap);
                if (detectionResult != null)
                {
                    document.PageAtIndex(0).Polygon = detectionResult.PointsNormalized;
                }
                
                Log.Info("CustomCamera", $"✓ Document created with UUID: {document.Uuid}");

                // Save to disk asynchronously (do not block processing thread)
                try
                {
                    // SaveCapturedDocumentFromBitmap performs file I/O; offload to background thread
                    var saveBitmap = pageBitmap;
                    var saveDetection = detectionResult;
                    var saveJpeg = jpegData;
                    Task.Run(() =>
                    {
                        try
                        {
                            long saveStart = JavaSystem.CurrentTimeMillis();
                            SaveCapturedDocumentFromBitmap(saveBitmap, saveJpeg, saveDetection);
                            long saveEnd = JavaSystem.CurrentTimeMillis();
                            Log.Info("CustomCamera", $"Save time: {saveEnd - saveStart} ms");
                        }
                        catch (Exception ex)
                        {
                            Log.Error("CustomCamera", $"Async save error: {ex.Message}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error("CustomCamera", $"✗ Failed to save still to disk: {ex.Message}");
                }

                RunOnUiThread(() =>
                {
                    _imageProcessingProgress.Visibility = ViewStates.Gone;
                    _isPhotoCapturing = false;
                    var intent = PagePreviewActivity.CreateIntent(this, document.Uuid);
                    StartActivity(intent);
                    Finish();
                });
            }
            catch (Exception ex)
            {
                Log.Error("CustomCamera", $"✗ Still processing error: {ex.Message}\n{ex.StackTrace}");
                _isPhotoCapturing = false;
                RunOnUiThread(() =>
                {
                    _imageProcessingProgress.Visibility = ViewStates.Gone;
                    ShowMessage("Failed to process image");
                });
            }
        }

        private bool ShowUserGuidance(DocumentDetectionStatus status)
        {
            string guidance = "";

            if (status == DocumentDetectionStatus.Ok)
            {
                guidance = "Ready to capture";
            }
            else if (status == DocumentDetectionStatus.OkButTooSmall)
            {
                guidance = "Move closer";
            }
            else if (status == DocumentDetectionStatus.OkButBadAngles)
            {
                guidance = "Bad angle";
            }
            else if (status == DocumentDetectionStatus.OkButBadAspectRatio)
            {
                guidance = "Rotate device";
            }
            else if (status == DocumentDetectionStatus.ErrorNothingDetected)
            {
                guidance = "No document";
            }
            else if (status == DocumentDetectionStatus.ErrorTooNoisy)
            {
                guidance = "Background too noisy";
            }
            else if (status == DocumentDetectionStatus.ErrorTooDark)
            {
                guidance = "Poor lighting";
            }
            else
            {
                guidance = "Positioning document...";
            }

            Color guidanceColor = status == DocumentDetectionStatus.Ok ? Color.Green : Color.Red;
            
            RunOnUiThread(() =>
            {
                _userGuidanceTextView.Text = guidance;
                _userGuidanceTextView.SetTextColor(Color.White);
                _userGuidanceTextView.SetBackgroundColor(guidanceColor);
            });

            return false;
        }

        internal void ShowMessage(string message)
        {
            RunOnUiThread(() =>
            {
                _userGuidanceTextView.Text = message;
                _userGuidanceTextView.SetTextColor(Color.White);
                _userGuidanceTextView.SetBackgroundColor(Color.Red);
            });
        }

        // Public helper so external callbacks can update UI safely without touching private fields
        public void OnCaptureFailedUi(string message)
        {
            RunOnUiThread(() =>
            {
                try
                {
                    _imageProcessingProgress.Visibility = ViewStates.Gone;
                    _isPhotoCapturing = false;
                    ShowMessage(message);
                }
                catch (Exception ex)
                {
                    Log.Error("CustomCamera", $"OnCaptureFailedUi error: {ex.Message}");
                }
            });
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Android.Content.PM.Permission[] grantResults)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

            Log.Debug("CustomCamera", $"OnRequestPermissionsResult: requestCode={requestCode}, permissions={string.Join(",", permissions)}, results={string.Join(",", grantResults.Select(g => g.ToString()))}");

            if (requestCode == CameraPermissionRequestCode)
            {
                bool allGranted = grantResults.Length > 0 && grantResults.All(g => g == Android.Content.PM.Permission.Granted);
                
                if (allGranted)
                {
                    Log.Info("CustomCamera", "✓ Camera permission granted");
                    // Camera is already initialized or will be when InitializeCamera() was called
                }
                else
                {
                    Log.Error("CustomCamera", "✗ Camera permission denied - cannot use camera");
                    ShowMessage("Camera permission is required");
                }
            }
            else if (requestCode == StoragePermissionRequestCode)
            {
                bool allGranted = grantResults.Length > 0 && grantResults.All(g => g == Android.Content.PM.Permission.Granted);
                
                if (allGranted)
                {
                    Log.Info("CustomCamera", "✓ Storage permission granted");
                    // Retry saving the document if we were waiting for permission
                    if (_pendingCapturedBitmap != null)
                    {
                        Log.Info("CustomCamera", "Retrying document save with storage permission (async)...");
                        var bmp = _pendingCapturedBitmap;
                        _pendingCapturedBitmap = null;
                        Task.Run(() =>
                        {
                            try
                            {
                                SaveCapturedDocument(bmp);
                                Log.Info("CustomCamera", "Async pending bitmap save completed");
                            }
                            catch (Exception ex)
                            {
                                Log.Error("CustomCamera", $"Async pending save error: {ex.Message}");
                            }
                            finally
                            {
                                try { bmp.Dispose(); } catch { }
                            }
                        });
                    }
                }
                else
                {
                    Log.Warn("CustomCamera", "⚠️ Storage permission denied - cannot save to disk");
                }
            }
        }

        private Bitmap _pendingCapturedBitmap;

        private bool CheckStoragePermission()
        {
            // Use WRITE_EXTERNAL_STORAGE for all API levels for document capture
            // MANAGE_EXTERNAL_STORAGE is only for special "All Files Access" use case
            return CheckSelfPermission(Android.Manifest.Permission.WriteExternalStorage) == Android.Content.PM.Permission.Granted;
        }

        private void RequestStoragePermission()
        {
            Log.Info("CustomCamera", "Requesting storage permission...");
            // Request WRITE_EXTERNAL_STORAGE for document storage on all API levels
            RequestPermissions(new[] { Android.Manifest.Permission.WriteExternalStorage }, StoragePermissionRequestCode);
        }

        private void SaveCapturedDocument(Bitmap bitmap)
        {
            try
            {
                if (bitmap == null)
                {
                    Log.Error("CustomCamera", "✗ SaveCapturedDocument: bitmap is null");
                    return;
                }
                // Deprecated - keep for compatibility. Use high-quality save helper instead.
                SaveCapturedDocumentFromBitmap(bitmap, null, null);
            }
            catch (Exception ex)
            {
                Log.Error("CustomCamera", $"✗ Failed to save document to disk: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void SaveCapturedDocumentFromBitmap(Bitmap bitmap, byte[] rawJpegData = null, DocumentDetectionResult detectionResult = null)
        {
            try
            {
                if (bitmap == null)
                {
                    Log.Error("CustomCamera", "SaveCapturedDocumentFromBitmap: bitmap is null");
                    return;
                }

                string outputDir = "/sdcard/IG/";
                if (!CheckStoragePermission())
                {
                    // Fallback to app-specific external cache directory to avoid permission requirement
                    var cachePath = this.ExternalCacheDir?.AbsolutePath;
                    if (!string.IsNullOrEmpty(cachePath))
                    {
                        outputDir = System.IO.Path.Combine(cachePath, "IG");
                    }
                }
                System.IO.Directory.CreateDirectory(outputDir);

                long timestamp = JavaSystem.CurrentTimeMillis();
                // Save raw JPEG if available; otherwise compress bitmap with max quality
                if (rawJpegData != null)
                {
                    string rawFilename = $"document_raw_{timestamp}.jpg";
                    string rawPath = System.IO.Path.Combine(outputDir, rawFilename);
                    System.IO.File.WriteAllBytes(rawPath, rawJpegData);
                    Log.Info("CustomCamera", $"✓ Raw JPEG saved: {rawPath}");
                }
                else
                {
                    string rawFilename = $"document_raw_{timestamp}.jpg";
                    string rawPath = System.IO.Path.Combine(outputDir, rawFilename);
                    using (var fos = new System.IO.FileStream(rawPath, System.IO.FileMode.Create))
                    {
                        bitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg, 100, fos);
                        fos.Flush();
                    }
                    Log.Info("CustomCamera", $"✓ Raw JPEG (from bitmap) saved: {rawPath}");
                }

                // If we have detection polygon, perform perspective crop and save as PNG (lossless)
                if (detectionResult != null && detectionResult.PointsNormalized != null && detectionResult.PointsNormalized.Count == 4)
                {
                    try
                    {
                        var crop = CropToPolygon(bitmap, detectionResult.PointsNormalized);
                        if (crop != null)
                        {
                            string cropFilename = $"document_crop_{timestamp}.png";
                            string cropPath = System.IO.Path.Combine(outputDir, cropFilename);
                            using (var fos = new System.IO.FileStream(cropPath, System.IO.FileMode.Create))
                            {
                                crop.Compress(Android.Graphics.Bitmap.CompressFormat.Png, 100, fos);
                                fos.Flush();
                            }
                            Log.Info("CustomCamera", $"✓ Cropped document saved (PNG): {cropPath}");
                            crop.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("CustomCamera", $"Failed to crop/save polygon: {ex.Message}");
                    }
                }

                Log.Info("CustomCamera", "✓ SaveCapturedDocumentFromBitmap completed");
                Log.Info("CustomCamera", $"Saved outputs to: {outputDir}");
            }
            catch (Exception ex)
            {
                Log.Error("CustomCamera", $"✗ SaveCapturedDocumentFromBitmap error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private Bitmap CropToPolygon(Bitmap source, IList<PointF> normalizedPolygon)
        {
            try
            {
                // Convert normalized points to pixel coordinates
                var pts = new float[8];
                for (int i = 0; i < 4; i++)
                {
                    pts[i * 2] = normalizedPolygon[i].X * source.Width;
                    pts[i * 2 + 1] = normalizedPolygon[i].Y * source.Height;
                }

                // Determine destination rectangle size using edge lengths
                // pts order: [0]=tl.x,[1]=tl.y,[2]=tr.x,[3]=tr.y,[4]=br.x,[5]=br.y,[6]=bl.x,[7]=bl.y
                float widthTop = Distance(pts[0], pts[1], pts[2], pts[3]);
                float widthBottom = Distance(pts[6], pts[7], pts[4], pts[5]);
                int dstWidth = (int)System.Math.Max(1, System.Math.Max(widthTop, widthBottom));

                float heightLeft = Distance(pts[0], pts[1], pts[6], pts[7]);
                float heightRight = Distance(pts[2], pts[3], pts[4], pts[5]);
                int dstHeight = (int)System.Math.Max(1, System.Math.Max(heightLeft, heightRight));

                // Source and destination arrays for matrix mapping: top-left, top-right, bottom-right, bottom-left
                var src = new float[] { pts[0], pts[1], pts[2], pts[3], pts[4], pts[5], pts[6], pts[7] };
                var dst = new float[] { 0f, 0f, dstWidth, 0f, dstWidth, dstHeight, 0f, dstHeight };

                var matrix = new Matrix();
                bool ok = matrix.SetPolyToPoly(src, 0, dst, 0, 4);
                if (!ok)
                {
                    Log.Warn("CustomCamera", "SetPolyToPoly failed for polygon transform - falling back to bounding-box crop");
                    // Fallback: crop axis-aligned bounding box around polygon
                    float minX = pts[0];
                    float minY = pts[1];
                    float maxX = pts[0];
                    float maxY = pts[1];
                    for (int i = 1; i < 4; i++)
                    {
                        float px = pts[i * 2];
                        float py = pts[i * 2 + 1];
                        if (px < minX) minX = px;
                        if (py < minY) minY = py;
                        if (px > maxX) maxX = px;
                        if (py > maxY) maxY = py;
                    }

                    int left = (int)System.Math.Max(0, System.Math.Floor(minX));
                    int top = (int)System.Math.Max(0, System.Math.Floor(minY));
                    int right = (int)System.Math.Min(source.Width, System.Math.Ceiling(maxX));
                    int bottom = (int)System.Math.Min(source.Height, System.Math.Ceiling(maxY));
                    int w = System.Math.Max(1, right - left);
                    int h = System.Math.Max(1, bottom - top);

                    try
                    {
                        return Bitmap.CreateBitmap(source, left, top, w, h);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("CustomCamera", $"Fallback bounding-box crop failed: {ex.Message}");
                        return null;
                    }
                }

                // For large output bitmaps, use tile-based rendering to reduce memory pressure
                const int TILE_SIZE = 512;
                long estimatedMemory = (long)dstWidth * dstHeight * 4; // ARGB_8888 = 4 bytes per pixel
                bool useTiling = estimatedMemory > (20 * 1024 * 1024); // Use tiling if output > 20MB

                if (useTiling)
                {
                    Log.Info("CustomCamera", $"Using tile-based rendering for large output: {dstWidth}x{dstHeight} (~{estimatedMemory / (1024 * 1024)}MB)");
                    return CropToPolygonTiled(source, matrix, dstWidth, dstHeight, TILE_SIZE);
                }

                Bitmap outBitmap = Bitmap.CreateBitmap(dstWidth, dstHeight, Bitmap.Config.Argb8888);
                var canvas = new Canvas(outBitmap);
                canvas.DrawBitmap(source, matrix, null);
                return outBitmap;
            }
            catch (Exception ex)
            {
                Log.Error("CustomCamera", $"CropToPolygon error: {ex.Message}");
                return null;
            }
        }

        private float Distance(float x1, float y1, float x2, float y2)
        {
            float dx = x2 - x1;
            float dy = y2 - y1;
            return (float)System.Math.Sqrt(dx * dx + dy * dy);
        }

        private Bitmap DecodeRegionForPolygon(byte[] jpegData, IList<PointF> normalizedPolygon)
        {
            try
            {
                // First, get the image dimensions without decoding
                var opts = new Android.Graphics.BitmapFactory.Options();
                opts.InJustDecodeBounds = true;
                Android.Graphics.BitmapFactory.DecodeByteArray(jpegData, 0, jpegData.Length, opts);
                int fullWidth = opts.OutWidth;
                int fullHeight = opts.OutHeight;

                // Calculate bounding box of the polygon with some padding
                float minX = 1f, minY = 1f, maxX = 0f, maxY = 0f;
                foreach (var pt in normalizedPolygon)
                {
                    if (pt.X < minX) minX = pt.X;
                    if (pt.Y < minY) minY = pt.Y;
                    if (pt.X > maxX) maxX = pt.X;
                    if (pt.Y > maxY) maxY = pt.Y;
                }

                // Add generous padding (15% of document size) to ensure we don't cut off edges
                // Also ensure minimum 50px padding in absolute terms
                float paddingX = System.Math.Max((maxX - minX) * 0.15f, 50f / fullWidth);
                float paddingY = System.Math.Max((maxY - minY) * 0.15f, 50f / fullHeight);
                minX = System.Math.Max(0f, minX - paddingX);
                minY = System.Math.Max(0f, minY - paddingY);
                maxX = System.Math.Min(1f, maxX + paddingX);
                maxY = System.Math.Min(1f, maxY + paddingY);

                // Convert to pixel coordinates
                int left = (int)(minX * fullWidth);
                int top = (int)(minY * fullHeight);
                int width = (int)((maxX - minX) * fullWidth);
                int height = (int)((maxY - minY) * fullHeight);

                // Check if region is significantly smaller than full image (>20% savings)
                float regionArea = (float)(width * height) / (fullWidth * fullHeight);
                if (regionArea > 0.8f)
                {
                    Log.Info("CustomCamera", $"Region covers {regionArea * 100:F1}% of image, using full decode");
                    return null; // Caller will fallback to full decode
                }

                Log.Info("CustomCamera", $"Decoding region: {width}x{height} ({regionArea * 100:F1}% of full image, saving {(1 - regionArea) * 100:F1}%)");

                // Use BitmapRegionDecoder for efficient region extraction
                using (var stream = new System.IO.MemoryStream(jpegData))
                {
                    var regionDecoder = BitmapRegionDecoder.NewInstance(stream, false);
                    if (regionDecoder == null)
                    {
                        Log.Warn("CustomCamera", "BitmapRegionDecoder.NewInstance returned null");
                        return null;
                    }

                    var rect = new Android.Graphics.Rect(left, top, left + width, top + height);
                    var regionOpts = new Android.Graphics.BitmapFactory.Options();
                    regionOpts.InPreferredConfig = Bitmap.Config.Argb8888;
                    
                    var regionBitmap = regionDecoder.DecodeRegion(rect, regionOpts);
                    regionDecoder.Dispose();
                    
                    return regionBitmap;
                }
            }
            catch (Exception ex)
            {
                Log.Error("CustomCamera", $"DecodeRegionForPolygon error: {ex.Message}");
                return null;
            }
        }

        private Bitmap CropToPolygonTiled(Bitmap source, Matrix transform, int dstWidth, int dstHeight, int tileSize)
        {
            try
            {
                // Create output bitmap
                Bitmap outBitmap = Bitmap.CreateBitmap(dstWidth, dstHeight, Bitmap.Config.Argb8888);
                var outCanvas = new Canvas(outBitmap);

                // Process in tiles to reduce peak memory
                int tilesX = (int)System.Math.Ceiling((double)dstWidth / tileSize);
                int tilesY = (int)System.Math.Ceiling((double)dstHeight / tileSize);
                
                Log.Info("CustomCamera", $"Rendering {tilesX}x{tilesY} tiles of {tileSize}x{tileSize}px");

                for (int ty = 0; ty < tilesY; ty++)
                {
                    for (int tx = 0; tx < tilesX; tx++)
                    {
                        int tileX = tx * tileSize;
                        int tileY = ty * tileSize;
                        int tileW = System.Math.Min(tileSize, dstWidth - tileX);
                        int tileH = System.Math.Min(tileSize, dstHeight - tileY);

                        // Create tile bitmap
                        using (var tileBitmap = Bitmap.CreateBitmap(tileW, tileH, Bitmap.Config.Argb8888))
                        {
                            var tileCanvas = new Canvas(tileBitmap);
                            
                            // Translate canvas to tile position, apply transform, then draw
                            tileCanvas.Translate(-tileX, -tileY);
                            tileCanvas.DrawBitmap(source, transform, null);
                            
                            // Copy tile to output
                            outCanvas.DrawBitmap(tileBitmap, tileX, tileY, null);
                        }
                        // Tile bitmap is disposed here, reducing memory pressure
                    }
                }

                return outBitmap;
            }
            catch (Exception ex)
            {
                Log.Error("CustomCamera", $"CropToPolygonTiled error: {ex.Message}");
                return null;
            }
        }

        protected override void OnResume()
        {
            Log.Info("CustomCamera", "▶ OnResume START");
            base.OnResume();
            StartBackgroundThread();
            
            // SurfaceView handles camera opening via SurfaceChanged callback
            Log.Debug("CustomCamera", "OnResume: waiting for SurfaceView to be ready");
            Log.Info("CustomCamera", "▶ OnResume END");
        }

        protected override void OnPause()
        {
            Log.Info("CustomCamera", "▶ OnPause START");
            Log.Debug("CustomCamera", "OnPause: Closing camera and stopping background thread");
            CloseCamera();
            StopBackgroundThread();
            base.OnPause();
            Log.Info("CustomCamera", "▶ OnPause END");
        }

        private void CloseCamera()
        {
            try
            {
                Log.Debug("CustomCamera", "Closing camera resources... (start)");

                // Close quick/foreground-safe handles directly
                try { _cameraCaptureSession?.Close(); } catch (Exception ex) { Log.Warn("CustomCamera", $"Close session failed: {ex.Message}"); }
                try { _cameraDevice?.Close(); } catch (Exception ex) { Log.Warn("CustomCamera", $"Close device failed: {ex.Message}"); }
                try { _previewSurface?.Release(); } catch (Exception ex) { Log.Warn("CustomCamera", $"Release preview surface failed: {ex.Message}"); }

                // Offload potentially blocking releases and closes (ImageReader) to background to avoid UI freeze
                var imgPreview = _imageReaderPreview;
                var imgPreviewSurf = _imageReaderPreviewSurface;
                var imgStill = _imageReaderStill;
                var imgStillSurf = _imageReaderStillSurface;

                _imageReaderPreview = null;
                _imageReaderPreviewSurface = null;
                _imageReaderStill = null;
                _imageReaderStillSurface = null;

                Task.Run(() =>
                {
                    try
                    {
                        long t0 = JavaSystem.CurrentTimeMillis();
                        try { imgPreviewSurf?.Release(); } catch (Exception) { }
                        try { imgPreview?.Close(); } catch (Exception) { }
                        try { imgStillSurf?.Release(); } catch (Exception) { }
                        try { imgStill?.Close(); } catch (Exception) { }
                        long t1 = JavaSystem.CurrentTimeMillis();
                        Log.Debug("CustomCamera", $"Background camera resource close done after {t1 - t0} ms");
                    }
                    catch (Exception ex)
                    {
                        Log.Error("CustomCamera", $"Background camera resource close error: {ex.Message}");
                    }
                });

                Log.Debug("CustomCamera", "CloseCamera scheduled background close for ImageReaders");
            }
            catch (Exception ex)
            {
                Log.Error("CustomCamera", $"Close camera error: {ex.Message}");
            }

            _isCameraInitialized = false;
        }

        protected override void OnDestroy()
        {
            Log.Debug("CustomCamera", "OnDestroy: Cleaning up");
            
            // Mark scanner as invalid BEFORE disposing to prevent background threads from using it
            _isScannerValid = false;
            
            CloseCamera();
            StopBackgroundThread();
            _documentScanner?.Dispose();
            base.OnDestroy();
        }
    }

    /// <summary>
    /// Callback for camera state changes (opened, disconnected, error).
    /// </summary>
    internal class CameraStateCallback : CameraDevice.StateCallback
    {
        private readonly CustomCameraPreviewActivity _activity;

        public CameraStateCallback(CustomCameraPreviewActivity activity)
        {
            _activity = activity;
        }

        public override void OnOpened(CameraDevice camera)
        {
            _activity.OnCameraOpened(camera);
        }

        public override void OnDisconnected(CameraDevice camera)
        {
            _activity.OnCameraDisconnected(camera);
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            _activity.OnCameraError(camera, error);
        }
    }

    /// <summary>
    /// Callback for capture session state changes.
    /// </summary>
    internal class CaptureSessionCallback : CameraCaptureSession.StateCallback
    {
        private readonly CustomCameraPreviewActivity _activity;
        private readonly CaptureRequest.Builder _previewRequestBuilder;

        public CaptureSessionCallback(CustomCameraPreviewActivity activity, CaptureRequest.Builder previewRequestBuilder)
        {
            _activity = activity;
            _previewRequestBuilder = previewRequestBuilder;
        }

        public override void OnConfigured(CameraCaptureSession session)
        {
            _activity.OnCaptureSessionCreated(session);
            _activity.OnCaptureSessionConfigured(_previewRequestBuilder.Build());
        }

        public override void OnConfigureFailed(CameraCaptureSession session)
        {
            Log.Error("CustomCamera", "Capture session configuration failed");
        }
    }

    /// <summary>
    /// Callback for ImageReader frame availability.
    /// </summary>
    internal class ImageAvailableListener : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        // Deprecated: replaced by PreviewImageAvailableListener and StillImageAvailableListener
        public void OnImageAvailable(ImageReader reader)
        {
            // No-op: kept for compatibility only
            var image = reader.AcquireLatestImage();
            if (image != null)
            {
                image.Close();
            }
        }
    }

    internal class PreviewImageAvailableListener : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        private readonly CustomCameraPreviewActivity _activity;

        public PreviewImageAvailableListener(CustomCameraPreviewActivity activity)
        {
            _activity = activity;
        }

        public void OnImageAvailable(ImageReader reader)
        {
            // Acquire image quickly and hand off heavy processing to the processing thread
            try
            {
                var image = reader.AcquireLatestImage();
                if (image == null)
                {
                    Log.Warn("CustomCamera", "PreviewImageReader: acquired image is null");
                    return;
                }

                // Throttle by time (keep same semantics)
                long currentTime = JavaSystem.CurrentTimeMillis();
                if (currentTime - _activity._lastFrameProcessingTime < _activity._frameProcessingIntervalMs)
                {
                    image.Close();
                    return;
                }

                _activity._lastFrameProcessingTime = currentTime;
                Log.Debug("CustomCamera", $"▶ Preview image available (fast path): {image.Width}x{image.Height}");

                // Copy YUV data into a local byte[] to minimize time holding the Image
                try
                {
                    var planes = image.GetPlanes();
                    int width = image.Width;
                    int height = image.Height;

                    // Simple conversion approach: extract Y plane and UV interleaved into a single buffer
                    var yPlane = planes[0];
                    var uPlane = planes[1];
                    var vPlane = planes[2];

                    int ySize = yPlane.Buffer.Remaining();
                    byte[] yData = new byte[ySize];
                    yPlane.Buffer.Get(yData);

                    int uSize = uPlane.Buffer.Remaining();
                    byte[] uData = new byte[uSize];
                    uPlane.Buffer.Get(uData);

                    int vSize = vPlane.Buffer.Remaining();
                    byte[] vData = new byte[vSize];
                    vPlane.Buffer.Get(vData);

                    // Capture pixel stride before closing the image — planes become invalid after image.Close()
                    int uvPixelStride = uPlane.PixelStride;

                    image.Close();

                    if (_activity._processingHandler != null)
                    {
                        _activity._processingHandler.Post(() =>
                        {
                            try
                            {
                                // Reconstruct a Bitmap on the processing thread using the same helper logic
                                var reconstructed = ConvertYuvToBitmapOnProcessingThread(yData, uData, vData, width, height, uvPixelStride);
                                if (reconstructed != null)
                                {
                                    if (_activity._isCameraInitialized)
                                    {
                                        _activity.ProcessFrameForDocumentDetection(reconstructed);
                                    }
                                    else
                                    {
                                        reconstructed.Dispose();
                                    }
                                }
                            }
                            catch (Exception inner)
                            {
                                Log.Error("CustomCamera", $"Processing thread preview decode error: {inner.Message}");
                            }
                        });
                    }
                    else
                    {
                        // No processing thread available: fall back to synchronous decode (slower)
                        var bitmap = ConvertImageToBitmapStaticFromPlanes(yData, uData, vData, width, height, uvPixelStride);
                        if (bitmap != null)
                        {
                            if (_activity._isCameraInitialized)
                            {
                                _activity.ProcessFrameForDocumentDetection(bitmap);
                            }
                            else
                            {
                                bitmap.Dispose();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    image.Close();
                    Log.Error("CustomCamera", $"Preview image fast-path error: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log.Error("CustomCamera", $"Preview image processing error: {ex.Message}");
            }
        }

        private static Bitmap ConvertImageToBitmapStatic(Image image)
        {
            try
            {
                int width = image.Width;
                int height = image.Height;

                Image.Plane yPlane = image.GetPlanes()[0];
                Image.Plane uPlane = image.GetPlanes()[1];
                Image.Plane vPlane = image.GetPlanes()[2];

                int ySize = yPlane.Buffer.Remaining();
                byte[] yData = new byte[ySize];
                yPlane.Buffer.Get(yData);

                int uvPixelStride = uPlane.PixelStride;
                int uvSize = uPlane.Buffer.Remaining();
                byte[] uvData = new byte[uvSize * 2];

                uPlane.Buffer.Get(uvData, 0, uvSize);
                vPlane.Buffer.Get(uvData, uvSize, uvSize);

                int[] pixels = new int[width * height];
                DecodeYUV420Static(yData, uvData, width, height, pixels, uvPixelStride);

                Bitmap bitmap = Bitmap.CreateBitmap(pixels, width, height, Bitmap.Config.Argb8888);
                return bitmap;
            }
            catch (Exception ex)
            {
                Log.Error("CustomCamera", $"Bitmap conversion error: {ex.Message}");
                return null;
            }
        }

        // Convert YUV planes to Bitmap on processing thread
        private static Bitmap ConvertYuvToBitmapOnProcessingThread(byte[] yData, byte[] uData, byte[] vData, int width, int height, int uvPixelStride)
        {
            try
            {
                // Reuse the decoder logic: combine u/v into uvData expected by DecodeYUV420Static
                int uvSize = uData.Length + vData.Length;
                byte[] uvData = new byte[uvSize];
                System.Array.Copy(uData, 0, uvData, 0, uData.Length);
                System.Array.Copy(vData, 0, uvData, uData.Length, vData.Length);

                int[] pixels = new int[width * height];
                DecodeYUV420Static(yData, uvData, width, height, pixels, uvPixelStride);
                Bitmap bitmap = Bitmap.CreateBitmap(pixels, width, height, Bitmap.Config.Argb8888);
                return bitmap;
            }
            catch (Exception ex)
            {
                Log.Error("CustomCamera", $"ConvertYuvToBitmapOnProcessingThread error: {ex.Message}");
                return null;
            }
        }

        private static Bitmap ConvertImageToBitmapStaticFromPlanes(byte[] yData, byte[] uData, byte[] vData, int width, int height, int uvPixelStride)
        {
            return ConvertYuvToBitmapOnProcessingThread(yData, uData, vData, width, height, uvPixelStride);
        }

        private static void DecodeYUV420Static(byte[] yData, byte[] uvData, int width, int height, int[] pixels, int uvPixelStride)
        {
            // Optimized YUV to RGB conversion using integer-only math (no floating-point)
            // Pre-computed coefficients: 1.402 -> 1436/1024, 0.344136 -> 352/1024, 0.714136 -> 731/1024, 1.772 -> 1815/1024
            int yIndex = 0;
            for (int i = 0; i < height; i++)
            {
                for (int j = 0; j < width; j++)
                {
                    int pixelIndex = i * width + j;
                    int y = (int)yData[yIndex++] & 0xff;

                    int uvRowIndex = (i >> 1); // Faster than i/2
                    int uvColIndex = (j >> 1); // Faster than j/2
                    int uvIndex = uvRowIndex * (width >> 1) + uvColIndex;

                    int u = (int)uvData[uvIndex] & 0xff;
                    int v = (int)uvData[uvIndex + ((width * height) >> 2)] & 0xff;

                    u -= 128;
                    v -= 128;

                    // Integer-only math (8x faster than floating-point)
                    int r = y + ((1436 * v) >> 10);
                    int g = y - ((352 * u + 731 * v) >> 10);
                    int b = y + ((1815 * u) >> 10);

                    r = r < 0 ? 0 : (r > 255 ? 255 : r);
                    g = g < 0 ? 0 : (g > 255 ? 255 : g);
                    b = b < 0 ? 0 : (b > 255 ? 255 : b);

                    pixels[pixelIndex] = (0xff << 24) | (r << 16) | (g << 8) | b;
                }
            }
        }
    }

    internal class StillImageAvailableListener : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        private readonly CustomCameraPreviewActivity _activity;

        public StillImageAvailableListener(CustomCameraPreviewActivity activity)
        {
            _activity = activity;
        }

        public void OnImageAvailable(ImageReader reader)
        {
            try
            {
                Log.Debug("CustomCamera", "[STILL-1] OnImageAvailable called - attempting to acquire image");
                
                var image = reader.AcquireLatestImage();
                if (image == null)
                {
                    Log.Warn("CustomCamera", "[STILL-2] StillImageReader: acquired image is null");
                    return;
                }

                Log.Debug("CustomCamera", $"[STILL-3] StillImageReader: image acquired {image.Width}x{image.Height}, awaitingStill={_activity._awaitingStill}");

                // For JPEG we can access the ByteBuffer directly; copy bytes quickly
                Log.Debug("CustomCamera", "[STILL-4] Extracting JPEG data from image plane");
                var plane = image.GetPlanes()[0];
                var buffer = plane.Buffer;
                byte[] data = new byte[buffer.Remaining()];
                buffer.Get(data);
                Log.Debug("CustomCamera", $"[STILL-5] Extracted {data.Length} bytes of JPEG data");
                
                image.Close();
                Log.Debug("CustomCamera", "[STILL-6] Image closed");

                Log.Debug("CustomCamera", $"[STILL-7] Checking processing handler: {(_activity._processingHandler != null ? "EXISTS" : "NULL")}");
                
                if (_activity._processingHandler != null)
                {
                    Log.Debug("CustomCamera", "[STILL-8] Posting to FRONT of processing queue (priority)...");
                    // Post raw JPEG bytes to FRONT of processing queue to bypass preview frame backlog
                    bool postResult = _activity._processingHandler.PostAtFrontOfQueue(() =>
                    {
                        try
                        {
                            Log.Debug("CustomCamera", $"[STILL-9] Still JPEG bytes posted to processing thread: {data.Length} bytes");
                            _activity._awaitingStill = false;
                            _activity.ProcessCapturedStill(data, null);
                        }
                        catch (Exception ex)
                        {
                            Log.Error("CustomCamera", $"[STILL-ERROR-A] Processing thread still post error: {ex.Message}\n{ex.StackTrace}");
                        }
                    });
                    Log.Debug("CustomCamera", $"[STILL-10] Posted to processing handler (returned: {postResult})");
                }
                else
                {
                    // No processing thread available: decode synchronously (fallback)
                    Log.Warn("CustomCamera", "[STILL-11] ⚠️ Processing handler is null - using synchronous decoding (fallback path)");
                    try
                    {
                        Log.Debug("CustomCamera", "[STILL-12] Starting synchronous JPEG decode...");
                        Bitmap bitmap = BitmapFactory.DecodeByteArray(data, 0, data.Length);
                        if (bitmap != null)
                        {
                            Log.Info("CustomCamera", $"[STILL-13] ✓ Still bitmap decoded: {bitmap.Width}x{bitmap.Height}");
                            _activity._awaitingStill = false;
                            Log.Debug("CustomCamera", "[STILL-14] Calling ProcessCapturedStill...");
                            _activity.ProcessCapturedStill(data, bitmap);
                            Log.Debug("CustomCamera", "[STILL-15] ProcessCapturedStill completed");
                        }
                        else
                        {
                            Log.Error("CustomCamera", "[STILL-ERROR-B] Decoded bitmap is null");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("CustomCamera", $"[STILL-ERROR-C] Synchronous still decode error: {ex.Message}\n{ex.StackTrace}");
                    }
                }
                
                Log.Debug("CustomCamera", "[STILL-16] OnImageAvailable completed successfully");
            }
            catch (Exception ex)
            {
                Log.Error("CustomCamera", $"[STILL-ERROR-D] Still image processing error: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    internal class CameraCaptureCallback : CameraCaptureSession.CaptureCallback
    {
        private readonly CustomCameraPreviewActivity _activity;

        public CameraCaptureCallback(CustomCameraPreviewActivity activity)
        {
            _activity = activity;
        }

        public override void OnCaptureCompleted(CameraCaptureSession session, CaptureRequest request, TotalCaptureResult result)
        {
            Log.Debug("CustomCamera", "Still capture completed");
            Log.Debug("CustomCamera", $"CaptureCompleted - awaitingStill={_activity._awaitingStill}");
            _activity.RunOnUiThread(() => { _activity.ShowMessage("Capture completed"); });
        }

        public override void OnCaptureFailed(CameraCaptureSession session, CaptureRequest request, CaptureFailure failure)
        {
            Log.Error("CustomCamera", $"Still capture failed: {failure}");
            _activity.OnCaptureFailedUi("Capture failed");
        }
    }

        // Note: legacy YUV-to-Bitmap helpers removed; PreviewImageAvailableListener provides static implementations now.
    }
