using Android.Graphics;
using Android.Widget;
using IO.Scanbot.Sdk.Document;
using IO.Scanbot.Sdk.Camera;
using System;

namespace ScanbotSdkExample.Droid.Views
{
    /// <summary>
    /// Handler for document detection results and user guidance feedback.
    /// This class manages the display of detection status, polygon overlay updates, and user guidance messages.
    /// </summary>
    public class DocumentDetectionHandler
    {
        private readonly DocumentPolygonOverlayView _polygonOverlay;
        private readonly TextView _userGuidanceTextView;
        private readonly Func<DocumentDetectionStatus, bool> _userGuidanceCallback;
        
        private long _lastGuidanceUpdateTime;
        private const long GuidanceUpdateIntervalMs = 500; // Update guidance at most every 500ms
        
        // Cache for point array conversion to reduce allocations
        private PointF[] _pointsCache = new PointF[4];

        public DocumentDetectionHandler(
            DocumentPolygonOverlayView polygonOverlay,
            TextView userGuidanceTextView,
            Func<DocumentDetectionStatus, bool> userGuidanceCallback)
        {
            _polygonOverlay = polygonOverlay ?? throw new ArgumentNullException(nameof(polygonOverlay));
            _userGuidanceTextView = userGuidanceTextView ?? throw new ArgumentNullException(nameof(userGuidanceTextView));
            _userGuidanceCallback = userGuidanceCallback ?? throw new ArgumentNullException(nameof(userGuidanceCallback));
            
            _lastGuidanceUpdateTime = 0;
        }

        /// <summary>
        /// Processes detection result from Scanbot SDK and updates UI elements accordingly.
        /// </summary>
        /// <param name="detectionResult">The document detection result containing polygon points and status</param>
        public void ShowDetectionStatus(DocumentDetectionResult detectionResult)
        {
            if (detectionResult == null)
            {
                return;
            }

            // Update polygon overlay with detected points
            if (detectionResult.PointsNormalized != null && detectionResult.PointsNormalized.Count > 0)
            {
                // Reuse cache if size matches, otherwise allocate new array
                int count = detectionResult.PointsNormalized.Count;
                if (_pointsCache == null || _pointsCache.Length != count)
                {
                    _pointsCache = new PointF[count];
                }
                
                for (int i = 0; i < count; i++)
                {
                    _pointsCache[i] = detectionResult.PointsNormalized[i];
                }
                _polygonOverlay.SetDetectedPolygon(_pointsCache);
                _polygonOverlay.SetDetectionStatus(detectionResult.Status);
            }

            // Update user guidance at throttled interval
            long currentTime = Java.Lang.JavaSystem.CurrentTimeMillis();
            if (currentTime - _lastGuidanceUpdateTime >= GuidanceUpdateIntervalMs)
            {
                _userGuidanceCallback?.Invoke(detectionResult.Status);
                _lastGuidanceUpdateTime = currentTime;
            }
        }

        /// <summary>
        /// Shows a simple text message in the guidance view (for errors or general messages).
        /// </summary>
        /// <param name="message">The message to display</param>
        public void ShowMessage(string message)
        {
            _userGuidanceTextView.Post(() =>
            {
                _userGuidanceTextView.Text = message;
                _userGuidanceTextView.SetTextColor(Color.White);
                _userGuidanceTextView.SetBackgroundColor(Color.Red);
            });
        }

        /// <summary>
        /// Clears the polygon overlay (called when document is no longer detected).
        /// </summary>
        public void ClearOverlay()
        {
            _polygonOverlay.ClearPolygon();
        }
    }
}
