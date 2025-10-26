using System;

namespace NinjaTrader.Custom.AddOns.OrderFlowBot.Events
{
    public class MessagingEvents
    {
        private readonly EventManager _eventManager;
        public event Func<string, string> OnGetAnalysis;

        public MessagingEvents(EventManager eventManager)
        {
            _eventManager = eventManager;
        }

        /// <summary>
        /// Event triggered when analysis is requested.
        /// Returns a JSON string from an external service.
        /// </summary>
        public string GetAnalysis(string metrics)
        {
            // Avoid ?.Invoke(...) — older compiler
            return _eventManager.InvokeEvent<string>(delegate
            {
                if (OnGetAnalysis != null)
                    return OnGetAnalysis(metrics);

                _eventManager.PrintMessage("OnGetAnalysis handler is null");
                return "{\"error\":\"no handler\"}";
            });
        }
    }
}
