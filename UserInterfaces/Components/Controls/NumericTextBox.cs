using NinjaTrader.Custom.AddOns.OrderFlowBot.UserInterfaces.Configs;
using NinjaTrader.Custom.AddOns.OrderFlowBot.UserInterfaces.Utils;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace NinjaTrader.Custom.AddOns.OrderFlowBot.UserInterfaces.Components.Controls
{
    public class NumericTextBox
    {
        public TextBox TextBox { get; private set; }

        private readonly DispatcherTimer _debounceTimer;
        public event EventHandler DebouncedTextChanged;

        public NumericTextBox(string toolTip)
        {
            var tb = new TextBox();
            tb.Height = 30;
            tb.Margin = new Thickness(3, 3, 3, 3);
            tb.ToolTip = toolTip;
            tb.VerticalContentAlignment = VerticalAlignment.Center;
            tb.Background = UserInterfaceUtils.GetSolidColorBrushFromHex(CustomColors.INPUT_FIELD_COLOR);
            tb.IsEnabled = false;

            // Key handling (navigation + edit keys)
            tb.PreviewKeyDown += OnPreviewKeyDown;

            // Text input validation
            tb.PreviewTextInput += OnPreviewTextInput;

            // Paste validation
            DataObject.AddPastingHandler(tb, OnPaste);

            // Debounce change notifications
            _debounceTimer = new DispatcherTimer();
            _debounceTimer.Interval = TimeSpan.FromMilliseconds(300);
            _debounceTimer.Tick += (s, e) =>
            {
                _debounceTimer.Stop();
                var handler = DebouncedTextChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            };

            tb.TextChanged += (s, e) =>
            {
                _debounceTimer.Stop();
                _debounceTimer.Start();
            };

            TextBox = tb;
        }

        // Allow navigation/edit keys; text validation happens in PreviewTextInput
        private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Left:
                case Key.Right:
                case Key.Up:
                case Key.Down:
                case Key.Home:
                case Key.End:
                case Key.Tab:
                case Key.Back:
                case Key.Delete:
                    // allow
                    e.Handled = false;
                    return;
                default:
                    // let PreviewTextInput handle character input;
                    // do not mark handled here
                    return;
            }
        }

        private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var tb = (TextBox)sender;

            // Simulate resulting text after input
            string current = tb.Text ?? string.Empty;
            int selStart = tb.SelectionStart;
            int selLen = tb.SelectionLength;

            string proposed = current.Remove(selStart, selLen)
                                     .Insert(selStart, e.Text);

            if (!IsValidNumericString(proposed))
            {
                e.Handled = true; // block
            }
            // else: let WPF insert normally
        }

        private void OnPaste(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(DataFormats.Text))
            {
                e.CancelCommand();
                return;
            }

            var tb = (TextBox)sender;
            string pasteText = e.DataObject.GetData(DataFormats.Text) as string ?? string.Empty;

            string current = tb.Text ?? string.Empty;
            int selStart = tb.SelectionStart;
            int selLen = tb.SelectionLength;

            string proposed = current.Remove(selStart, selLen)
                                     .Insert(selStart, pasteText);

            if (!IsValidNumericString(proposed))
                e.CancelCommand();
        }

        /// <summary>
        /// Valid formats:
        ///   optional leading '-', digits, optional single '.', up to two digits after '.'
        ///   empty string is OK (user still typing)
        /// </summary>
        private static bool IsValidNumericString(string s)
        {
            if (string.IsNullOrEmpty(s))
                return true; // allow user to clear/start

            int i = 0;
            int len = s.Length;

            // optional leading '-'
            if (s[0] == '-')
            {
                i = 1;
                if (len == 1) return true; // just "-"
            }

            bool seenDigit = false;
            bool seenDot = false;
            int digitsAfterDot = 0;

            for (; i < len; i++)
            {
                char c = s[i];

                if (c >= '0' && c <= '9')
                {
                    if (seenDot)
                    {
                        digitsAfterDot++;
                        // enforce two decimals max
                        if (digitsAfterDot > 2) return false;
                    }
                    seenDigit = true;
                    continue;
                }

                if (c == '.')
                {
                    if (seenDot) return false; // only one dot
                    seenDot = true;
                    // if dot is first char after optional '-', require at least one digit eventually
                    continue;
                }

                // any other character invalid
                return false;
            }

            // if string is just "-" or ".", allow (user typing), otherwise require at least one digit somewhere
            if (!seenDigit)
                return (len == 1 && (s[0] == '-' || s[0] == '.'));

            return true;
        }
    }
}
