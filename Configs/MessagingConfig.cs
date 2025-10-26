using NinjaTrader.Custom.AddOns.OrderFlowBot.States;

namespace NinjaTrader.Custom.AddOns.OrderFlowBot.Configs
{
    public class MessagingConfig
    {
        private static readonly MessagingConfig _instance = new MessagingConfig();

        private MessagingConfig()
        {
        }

        // was: public static MessagingConfig Instance => _instance;
        public static MessagingConfig Instance
        {
            get { return _instance; }
        }

        public EnvironmentType MarketEnvironment { get; set; }
        public string ExternalAnalysisService { get; set; }
        public bool ExternalAnalysisServiceEnabled { get; set; }
    }
}
