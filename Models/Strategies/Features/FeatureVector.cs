using System;

namespace NinjaTrader.Custom.AddOns.OrderFlowBot.Models.Strategies.Features
{
    /// <summary>
    /// Compact struct containing features for ML inference and regime detection.
    /// Features are normalized to [0,1] range for consistent ML input.
    /// </summary>
    public struct FeatureVector
    {
        // === Econophysics Features ===
        /// <summary>1-minute log return (normalized)</summary>
        public float Ret1m;
        
        /// <summary>Rolling realized volatility z-score (normalized)</summary>
        public float RvZ;
        
        /// <summary>AR(1) autocorrelation - critical slowing down indicator (normalized)</summary>
        public float Ar1;
        
        /// <summary>CVD slope (cumulative delta velocity) (normalized)</summary>
        public float CvdSlope;
        
        /// <summary>Shannon entropy of signed returns (8 bins, normalized)</summary>
        public float Entropy;
        
        /// <summary>Hurst exponent (R/S estimator) (normalized)</summary>
        public float Hurst;
        
        /// <summary>Order imbalance proxy from data bars (normalized)</summary>
        public float Imb;
        
        /// <summary>Stress index [0..1] - fused regime indicator</summary>
        public float Stress;

        /// <summary>
        /// Convert to float array for ML inference
        /// </summary>
        public float[] ToArray()
        {
            return new float[]
            {
                Ret1m, RvZ, Ar1, CvdSlope, Entropy, Hurst, Imb, Stress
            };
        }

        /// <summary>
        /// Create from float array (for ML output parsing)
        /// </summary>
        public static FeatureVector FromArray(float[] features)
        {
            if (features == null || features.Length < 8)
                throw new ArgumentException("Feature array must have at least 8 elements");

            return new FeatureVector
            {
                Ret1m = features[0],
                RvZ = features[1],
                Ar1 = features[2],
                CvdSlope = features[3],
                Entropy = features[4],
                Hurst = features[5],
                Imb = features[6],
                Stress = features[7]
            };
        }

        /// <summary>
        /// Validate that all features are in valid range [0,1]
        /// </summary>
        public bool IsValid()
        {
            return Ret1m >= 0f && Ret1m <= 1f &&
                   RvZ >= 0f && RvZ <= 1f &&
                   Ar1 >= 0f && Ar1 <= 1f &&
                   CvdSlope >= 0f && CvdSlope <= 1f &&
                   Entropy >= 0f && Entropy <= 1f &&
                   Hurst >= 0f && Hurst <= 1f &&
                   Imb >= 0f && Imb <= 1f &&
                   Stress >= 0f && Stress <= 1f;
        }

        /// <summary>
        /// Clamp all features to [0,1] range
        /// </summary>
        public FeatureVector Clamp()
        {
            return new FeatureVector
            {
                Ret1m = Math.Max(0f, Math.Min(1f, Ret1m)),
                RvZ = Math.Max(0f, Math.Min(1f, RvZ)),
                Ar1 = Math.Max(0f, Math.Min(1f, Ar1)),
                CvdSlope = Math.Max(0f, Math.Min(1f, CvdSlope)),
                Entropy = Math.Max(0f, Math.Min(1f, Entropy)),
                Hurst = Math.Max(0f, Math.Min(1f, Hurst)),
                Imb = Math.Max(0f, Math.Min(1f, Imb)),
                Stress = Math.Max(0f, Math.Min(1f, Stress))
            };
        }

        public override string ToString()
        {
            return "FeatureVector(Ret1m=" + Ret1m.ToString("F3") + ", RvZ=" + RvZ.ToString("F3") + ", Ar1=" + Ar1.ToString("F3") + ", CvdSlope=" + CvdSlope.ToString("F3") + ", " +
                   "Entropy=" + Entropy.ToString("F3") + ", Hurst=" + Hurst.ToString("F3") + ", Imb=" + Imb.ToString("F3") + ", Stress=" + Stress.ToString("F3") + ")";
        }
    }
}
