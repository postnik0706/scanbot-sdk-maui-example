using Android.Content;
using Android.Graphics;
using Android.Util;
using Android.Views;
using Android.Runtime;
using IO.Scanbot.Sdk.Camera;
using IO.Scanbot.Sdk.Document;
using Java.Lang;
using System;
using Math = Java.Lang.Math;
using Path = Android.Graphics.Path;

namespace ScanbotSdkExample.Droid.Views
{
    /// <summary>
    /// Custom view that renders the detected document polygon overlay on top of the camera preview.
    /// This view displays real-time detection results with animated polygon boundaries showing
    /// the detected document corners.
    /// </summary>
    [Register("ScanbotSdkExample.Droid.Views.DocumentPolygonOverlayView")]
    public class DocumentPolygonOverlayView : View
    {
        /// <summary>Debug logging tag</summary>
        internal const string LogTag = "CustomCamera";

        private Paint _polygonPaint;
        private Paint _cornerPaint;
        private Paint _cornerRadiusPaint;

        private PointF[] _detectedPoints;
        private bool _isDetectionGood;
        
        private float _cornerRadius = 15f;
        private float _lineStrokeWidth = 3f;
        
        // Cache for array conversion to avoid allocations in hot path
        private PointF[] _pointsCache = new PointF[4];
        
        private const int GoodDetectionColor = unchecked((int)0xFF00FF00); // Green
        private const int PoorDetectionColor = unchecked((int)0xFFFF0000);  // Red
        private const int CornerPointColor = unchecked((int)0xFFFFFFFF);    // White
        
        // SOLUTION: Track actual preview frame dimensions for correct coordinate scaling
        private int _previewFrameWidth = 0;
        private int _previewFrameHeight = 0;

        public DocumentPolygonOverlayView(Context context) : base(context)
        {
            Android.Util.Log.Debug(LogTag, "DocumentPolygonOverlayView(Context) constructor called");
            Initialize();
        }

        public DocumentPolygonOverlayView(Context context, IAttributeSet attrs) : base(context, attrs)
        {
            Android.Util.Log.Debug(LogTag, "DocumentPolygonOverlayView(Context, IAttributeSet) constructor called");
            Initialize();
        }

        public DocumentPolygonOverlayView(Context context, IAttributeSet attrs, int defStyleAttr) : base(context, attrs, defStyleAttr)
        {
            Android.Util.Log.Debug(LogTag, "DocumentPolygonOverlayView(Context, IAttributeSet, int) constructor called");
            Initialize();
        }

        private void Initialize()
        {
            Android.Util.Log.Debug(LogTag, "Initialize() called");
            
            SetWillNotDraw(false);
            Android.Util.Log.Debug(LogTag, "SetWillNotDraw(false) - overlay will handle OnDraw");
            
            SetBackgroundColor(Android.Graphics.Color.Transparent);
            Android.Util.Log.Debug(LogTag, $"SetBackgroundColor(Transparent) - color value: {Android.Graphics.Color.Transparent}");
            
            // Ensure proper transparency handling - disable GPU acceleration for this overlay view
            SetLayerType(LayerType.Software, null);
            Android.Util.Log.Debug(LogTag, "SetLayerType(Software, null) - disabled GPU acceleration for transparency");

            // Paint for polygon outline
            _polygonPaint = new Paint
            {
                Color = new Color(PoorDetectionColor),
                StrokeWidth = _lineStrokeWidth,
                AntiAlias = true
            };
            _polygonPaint.SetStyle(Paint.Style.Stroke);

            // Note: polygon fill removed to keep document fully visible; only stroke and corners drawn.

            // Paint for corner points
            _cornerPaint = new Paint
            {
                Color = new Color(CornerPointColor),
                StrokeWidth = _lineStrokeWidth,
                AntiAlias = true
            };
            _cornerPaint.SetStyle(Paint.Style.Stroke);

            // Paint for corner radius circle
            _cornerRadiusPaint = new Paint
            {
                Color = new Color(CornerPointColor),
                AntiAlias = true,
                Alpha = 200  // ~80% opacity for corner points
            };
            _cornerRadiusPaint.SetStyle(Paint.Style.Fill);
        }

        /// <summary>
        /// Sets the detected polygon points (normalized coordinates between 0 and 1).
        /// The points should be ordered: top-left, top-right, bottom-right, bottom-left.
        /// </summary>
        public void SetDetectedPolygon(PointF[] normalizedPoints)
        {
            if (normalizedPoints == null || normalizedPoints.Length < 4)
            {
                Android.Util.Log.Debug(LogTag, $"SetDetectedPolygon: invalid points (null or length={normalizedPoints?.Length})");
                ClearPolygon();
                return;
            }

            // Reduced logging for performance: only log when frame dimensions change
            // Original verbose logging caused hundreds of log calls per second on hot path

            // SOLUTION: Convert normalized coordinates using preview frame dimensions, then scale to view coordinates
            // This ensures correct mapping even when view dimensions differ from frame dimensions
            
            // Reuse array if size matches to avoid allocation
            if (_detectedPoints == null || _detectedPoints.Length != normalizedPoints.Length)
            {
                _detectedPoints = new PointF[normalizedPoints.Length];
            }
            
            if (_previewFrameWidth > 0 && _previewFrameHeight > 0)
            {
                // Use actual preview frame dimensions for accurate normalized->pixel conversion
                float scaleX = (float)Width / _previewFrameWidth;
                float scaleY = (float)Height / _previewFrameHeight;
                
                // Coordinate transformation without logging (hot path optimization)
                for (int i = 0; i < normalizedPoints.Length; i++)
                {
                    float framePixelX = normalizedPoints[i].X * _previewFrameWidth;
                    float framePixelY = normalizedPoints[i].Y * _previewFrameHeight;
                    
                    // normalized (0-1) -> frame pixels (0-width/height) -> view coordinates
                    _detectedPoints[i] = new PointF(
                        framePixelX * scaleX,
                        framePixelY * scaleY
                    );
                }
            }
            else
            {
                // Fallback: use view dimensions directly (old behavior)
                string fallbackMsg = $"⚠️ Preview frame dimensions NOT SET (_previewFrameWidth={_previewFrameWidth}, _previewFrameHeight={_previewFrameHeight}) - using view dimensions as fallback";
                Android.Util.Log.Warn(LogTag, fallbackMsg);
                for (int i = 0; i < normalizedPoints.Length; i++)
                {
                    _detectedPoints[i] = new PointF(
                        normalizedPoints[i].X * Width,
                        normalizedPoints[i].Y * Height
                    );
                    Android.Util.Log.Warn(LogTag, $"  Point {i}: norm({normalizedPoints[i].X:F4},{normalizedPoints[i].Y:F4}) " +
                                                     $"-> viewPx({_detectedPoints[i].X:F1},{_detectedPoints[i].Y:F1}) [FALLBACK - NO FRAME DIMS]");
                }
            }

            _isDetectionGood = true;
            Invalidate();
        }

        /// <summary>
        /// Sets the actual preview frame dimensions for correct coordinate scaling.
        /// This should be called with the actual camera frame dimensions (e.g., 4000x3000).
        /// </summary>
        public void SetPreviewFrameDimensions(int frameWidth, int frameHeight)
        {
            if (_previewFrameWidth != frameWidth || _previewFrameHeight != frameHeight)
            {
                Android.Util.Log.Debug(LogTag, $"✓ Preview frame dimensions set: {_previewFrameWidth}x{_previewFrameHeight} -> {frameWidth}x{frameHeight}");
                _previewFrameWidth = frameWidth;
                _previewFrameHeight = frameHeight;
                
                // Recalculate points if already set
                if (_detectedPoints != null)
                {
                    Android.Util.Log.Debug(LogTag, "Recalculating polygon coordinates for new frame dimensions");
                    Invalidate();
                }
            }
            else
            {
                Android.Util.Log.Debug(LogTag, $"Preview frame dimensions unchanged: {frameWidth}x{frameHeight}");
            }
        }

        /// <summary>
        /// Sets the detection status to update polygon color.
        /// Good status (Ok) shows green, all other statuses show red.
        /// </summary>
        public void SetDetectionStatus(DocumentDetectionStatus status)
        {
            // Only show green for perfect Ok status, all others are red/warning
            _isDetectionGood = (status == DocumentDetectionStatus.Ok);
            
            int color = _isDetectionGood ? GoodDetectionColor : PoorDetectionColor;
            _polygonPaint.Color = new Color(color);
            
            Invalidate();
        }

        /// <summary>
        /// Clears the detected polygon from the view.
        /// </summary>
        public void ClearPolygon()
        {
            _detectedPoints = null;
            _isDetectionGood = false;
            Invalidate();
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);

            if (_detectedPoints == null || _detectedPoints.Length < 4)
            {
                return;
            }

            // OnDraw is called at 30+ FPS - logging removed for performance
            // Previous implementation generated hundreds of log calls per second

            // Draw polygon path (stroke only - no fill to keep document fully visible)
            var path = new Path();
            path.MoveTo(_detectedPoints[0].X, _detectedPoints[0].Y);

            for (int i = 1; i < _detectedPoints.Length; i++)
            {
                path.LineTo(_detectedPoints[i].X, _detectedPoints[i].Y);
            }

            path.Close();

            // Do not draw a filled overlay — only draw the outline so the document remains visible
            canvas.DrawPath(path, _polygonPaint);

            // Draw corner points with circles
            foreach (var point in _detectedPoints)
            {
                canvas.DrawCircle(point.X, point.Y, _cornerRadius, _cornerRadiusPaint);
                canvas.DrawCircle(point.X, point.Y, _cornerRadius, _cornerPaint);
            }
        }

        protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
        {
            base.OnSizeChanged(w, h, oldw, oldh);
            
            Android.Util.Log.Debug(LogTag, $"OnSizeChanged: old=({oldw}x{oldh}), new=({w}x{h})");
            
            // Clear points if size changes as coordinates need recalculation
            if (oldw != w || oldh != h)
            {
                _detectedPoints = null;
                Android.Util.Log.Debug(LogTag, "Size changed - cleared detected points");
            }
        }
    }
}
