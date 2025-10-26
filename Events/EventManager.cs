using System;

namespace NinjaTrader.Custom.AddOns.OrderFlowBot.Events
{
    public class EventManager
    {
        public event Action<string, bool> OnPrintMessage;

        public void InvokeEvent(Action eventHandler)
        {
            try
            {
                if (eventHandler != null)
                    eventHandler();
            }
            catch (Exception ex)
            {
                PrintMessage("Error invoking event: " + ex.Message);
            }
        }

        public void InvokeEvent<T>(Action<T> eventHandler, T arg)
        {
            try
            {
                if (eventHandler != null)
                    eventHandler(arg);
            }
            catch (Exception ex)
            {
                PrintMessage("Error invoking event: " + ex.Message);
            }
        }

        public void InvokeEvent<T1, T2>(Action<T1, T2> eventHandler, T1 arg1, T2 arg2)
        {
            try
            {
                if (eventHandler != null)
                    eventHandler(arg1, arg2);
            }
            catch (Exception ex)
            {
                PrintMessage("Error invoking event: " + ex.Message);
            }
        }

        public T InvokeEvent<T>(Func<T> eventHandler)
        {
            try
            {
                if (eventHandler != null)
                    return eventHandler();

                PrintMessage("Event handler is null");
                return default(T);
            }
            catch (Exception ex)
            {
                PrintMessage("Error invoking event: " + ex.Message);
                return default(T);
            }
        }

        public void PrintMessage(string eventMessage, bool addNewLine = false)
        {
            InvokeEvent(OnPrintMessage, eventMessage, addNewLine);
        }
    }
}
