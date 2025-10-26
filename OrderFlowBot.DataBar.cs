using NinjaTrader.Custom.AddOns.OrderFlowBot.Configs;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Models.DataBars;
using NinjaTrader.Custom.AddOns.OrderFlowBot.Models.DataBars.Base;
using NinjaTrader.Custom.AddOns.OrderFlowBot.UserInterfaces.Configs;
using NinjaTrader.NinjaScript.BarsTypes;
using NinjaTrader.NinjaScript.Indicators;
using System;
using System.Collections.Generic;
using System.IO;

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class OrderFlowBot : Strategy
    {
        private DataBarDataProvider _dataBarDataProvider;
        // NOTE: do NOT redeclare _cumulativeDelta here if it already exists in another partial.

        private void InitializeDataBar()
        {
            _dataBarDataProvider = new DataBarDataProvider();
        }

        private IDataBarDataProvider GetDataBarDataProvider(IDataBarConfig config, int barsAgo = 0)
        {
            _dataBarDataProvider.Time = ToTime(Time[barsAgo]);
            _dataBarDataProvider.Day = ToDay(Time[barsAgo]);
            _dataBarDataProvider.CurrentBar = CurrentBars[0];
            _dataBarDataProvider.BarsAgo = barsAgo;
            _dataBarDataProvider.High = High[barsAgo];
            _dataBarDataProvider.Low = Low[barsAgo];
            _dataBarDataProvider.Open = Open[barsAgo];
            _dataBarDataProvider.Close = Close[barsAgo];

            VolumetricBarsType volumetricBar = Bars.BarsSeries.BarsType as VolumetricBarsType;
            _dataBarDataProvider.VolumetricBar = PopulateCustomVolumetricBar(volumetricBar, config);

            try
            {
                _dataBarDataProvider.CumulativeDeltaBar = new CumulativeDeltaBar
                {
                    Open = _cumulativeDelta.DeltaOpen[barsAgo],
                    Close = _cumulativeDelta.DeltaClose[barsAgo],
                    High = _cumulativeDelta.DeltaHigh[barsAgo],
                    Low  = _cumulativeDelta.DeltaLow[barsAgo]
                };
            }
            catch
            {
                // Fallback
                _dataBarDataProvider.CumulativeDeltaBar = new CumulativeDeltaBar();
            }

            return _dataBarDataProvider;
        }

        private ICustomVolumetricBar PopulateCustomVolumetricBar(VolumetricBarsType volumetricBar, IDataBarConfig config)
        {
            CustomVolumetricBar customBar = new CustomVolumetricBar();

            // Guard: if not a Volumetric bar type, return an empty bar
            if (volumetricBar == null || volumetricBar.Volumes == null)
                return customBar;

            double high = _dataBarDataProvider.High;
            double low  = _dataBarDataProvider.Low;

           	int idx = _dataBarDataProvider.CurrentBar - _dataBarDataProvider.BarsAgo;
			if (idx < 0 || idx >= volumetricBar.Volumes.Length)   // <- Length
    		return customBar;

			var volumes = volumetricBar.Volumes[idx];

            customBar.TotalVolume        = volumes.TotalVolume;
            customBar.TotalBuyingVolume  = volumes.TotalBuyingVolume;
            customBar.TotalSellingVolume = volumes.TotalSellingVolume;

            double pointOfControl;
            volumes.GetMaximumVolume(null, out pointOfControl);
            customBar.PointOfControl = pointOfControl;

            // Get bid/ask volume for each price in bar
            List<BidAskVolume> bidAskVolumeList = new List<BidAskVolume>();
            int ticksPerLevel = config.TicksPerLevel;
            int totalLevels   = 0;
            int counter       = 0;

            while (high >= low)
            {
                if (counter == 0)
                {
                    BidAskVolume bav = new BidAskVolume();
                    bav.Price     = high;
                    bav.BidVolume = volumes.GetBidVolumeForPrice(high);
                    bav.AskVolume = volumes.GetAskVolumeForPrice(high);
                    bidAskVolumeList.Add(bav);
                }

                if (counter == ticksPerLevel - 1)
                    counter = 0;
                else
                    counter++;

                totalLevels++;
                high -= config.TickSize;
            }

            // Nudge alignment when levels don't divide evenly
            if (totalLevels % ticksPerLevel > 0 && bidAskVolumeList.Count > 4)
                bidAskVolumeList.RemoveAt(0);

            customBar.BidAskVolumes = bidAskVolumeList;

            // Deltas
            customBar.BarDelta         = volumes.BarDelta;
            customBar.MinSeenDelta     = volumes.MinSeenDelta;
            customBar.MaxSeenDelta     = volumes.MaxSeenDelta;
            customBar.DeltaSh          = volumes.DeltaSh;
            customBar.DeltaSl          = volumes.DeltaSl;
            customBar.CumulativeDelta  = volumes.CumulativeDelta;
            customBar.DeltaPercentage  = Math.Round(volumes.GetDeltaPercent(), 2);

            // Guard previous index for first bar to avoid OOB
          	int prevIdx = idx - 1;
			if (prevIdx >= 0 && prevIdx < volumetricBar.Volumes.Length)  // <- Length
    		customBar.DeltaChange = volumes.BarDelta - volumetricBar.Volumes[prevIdx].BarDelta;
			else
    		customBar.DeltaChange = 0;

            return customBar;
        }

        private void SetConfigs()
        {
            DataBarConfig.Instance.TicksPerLevel       = TicksPerLevel;
            DataBarConfig.Instance.TickSize            = TickSize;
            DataBarConfig.Instance.StackedImbalance    = StackedImbalance;
            DataBarConfig.Instance.ImbalanceRatio      = ImbalanceRatio;
            DataBarConfig.Instance.ImbalanceMinDelta   = ImbalanceMinDelta;
            DataBarConfig.Instance.ValueAreaPercentage = ValueAreaPercentage;
            DataBarConfig.Instance.TrainingDataDirectory = TrainingDataDirectory;
            DataBarConfig.Instance.Target = Target;
            DataBarConfig.Instance.Stop   = Stop;

            UserInterfaceConfig.Instance.AssetsPath =
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                             "NinjaTrader 8", "bin", "Custom", "AddOns", "OrderFlowBot", "Assets");
        }
    }
}
