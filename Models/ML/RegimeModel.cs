using System;
using System.IO;
// Note: Microsoft.ML.OnnxRuntime is not available in NinjaTrader 8
// This implementation provides graceful fallback when ONNX is not available

namespace NinjaTrader.Custom.AddOns.OrderFlowBot.Models.ML
{
    /// <summary>
    /// ONNX runtime helper for ML inference in FMS strategy.
    /// Loads C:\ofb_models\ofb_model.onnx and provides prediction interface.
    /// Note: ONNX functionality is disabled in NinjaTrader 8 due to assembly limitations.
    /// </summary>
    public class RegimeModel : IDisposable
    {
        private bool _disposed = false;

        /// <summary>
        /// Whether the model is successfully loaded and ready for inference
        /// </summary>
        public bool IsLoaded { get { return false; } } // Always false in NT8

        /// <summary>
        /// Initialize with default model path
        /// </summary>
        public RegimeModel() : this(@"C:\ofb_models\ofb_model.onnx")
        {
        }

        /// <summary>
        /// Initialize with custom model path
        /// </summary>
        public RegimeModel(string modelPath)
        {
            // ONNX functionality disabled in NinjaTrader 8
            // This is a placeholder implementation
        }

        /// <summary>
        /// Predict ML score from feature vector
        /// </summary>
        /// <param name="features">8-element feature array [ret1m, rvZ, ar1, cvdSlope, entropy, hurst, imb, stress]</param>
        /// <returns>ML prediction score [0,1] or 0 if model not loaded</returns>
        public float Predict(float[] features)
        {
            // ONNX functionality disabled in NinjaTrader 8
            // Return neutral score (0.5) as fallback
            return 0.5f;
        }

        /// <summary>
        /// Predict ML score from FeatureVector struct
        /// </summary>
        /// <param name="features">Feature vector</param>
        /// <returns>ML prediction score [0,1] or 0 if model not loaded</returns>
        public float Predict(Models.Strategies.Features.FeatureVector features)
        {
            return Predict(features.ToArray());
        }

        /// <summary>
        /// Get model metadata for debugging
        /// </summary>
        public string GetModelInfo()
        {
            return "ONNX functionality disabled in NinjaTrader 8 - using FastAPI service instead";
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Nothing to dispose in NT8 implementation
                }
                _disposed = true;
            }
        }

        ~RegimeModel()
        {
            Dispose(false);
        }
    }
}
