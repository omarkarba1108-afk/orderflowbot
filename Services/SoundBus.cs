using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NinjaTrader.Core;

namespace NinjaTrader.Custom.AddOns.OrderFlowBot.Services
{
    public static class SoundBus
    {
        private static readonly object gate = new object();
        private static MediaPlayer player;
        private static DateTime lastPlay = DateTime.MinValue;

        public static void Play(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) 
            {
                System.Diagnostics.Debug.WriteLine("[SoundBus] Path is null or empty");
                return;
            }

            // Expand relative filenames to our AddOn Assets folder if needed
            if (!Path.IsPathRooted(path))
                path = Path.Combine(Globals.UserDataDir, "bin", "Custom", "AddOns", "OrderFlowBot", "Assets", path);

            System.Diagnostics.Debug.WriteLine(string.Format("[SoundBus] Trying to play: {0}", path));
            
            if (!File.Exists(path)) 
            {
                System.Diagnostics.Debug.WriteLine(string.Format("[SoundBus] File not found: {0}", path));
                // Try to play a system sound as fallback
                try
                {
                    System.Media.SystemSounds.Beep.Play();
                    System.Diagnostics.Debug.WriteLine("[SoundBus] Played system beep as fallback");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(string.Format("[SoundBus] Fallback sound failed: {0}", ex.Message));
                }
                return;
            }
            
            System.Diagnostics.Debug.WriteLine(string.Format("[SoundBus] File found, playing: {0}", path));

            // Use MediaPlayer for both WAV and MP3 files
            if (Application.Current != null && Application.Current.Dispatcher != null)
                Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    lock (gate)
                    {
                        // simple debounce so multiple events in same tick don't overlap
                        if ((DateTime.UtcNow - lastPlay).TotalMilliseconds < 150) return;
                        lastPlay = DateTime.UtcNow;

                        if (player == null)
                        {
                            player = new MediaPlayer();
                            player.MediaEnded += (_, __) => { try { player.Stop(); } catch { } };
                        }

                        player.Open(new Uri(path));
                        player.Volume = 1.0;
                        player.Position = TimeSpan.Zero;
                        player.Play();
                    }
                }
                catch { }
            }));
        }
    }
}
