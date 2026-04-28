using System.Drawing;
using MusicBeePlugin.Interfaces;
using static MusicBeePlugin.Interfaces.Plugin;

namespace MusicBee.AI.Search.Ui.WinForms
{
    /// <summary>
    /// Resolves the colours used by ChatPanel from MusicBee's active skin,
    /// falling back to a dark default palette when running outside MusicBee
    /// (e.g. the Console host) or when the API call fails.
    /// </summary>
    public sealed class MbTheme
    {
        public Color Background { get; private set; } = Color.FromArgb(31, 31, 35);
        public Color BackgroundAlt { get; private set; } = Color.FromArgb(38, 38, 44);
        public Color InputBackground { get; private set; } = Color.FromArgb(43, 43, 48);
        public Color Border { get; private set; } = Color.FromArgb(58, 58, 64);
        public Color Foreground { get; private set; } = Color.FromArgb(238, 238, 238);
        public Color ForegroundDim { get; private set; } = Color.FromArgb(156, 156, 168);
        public Color ButtonBackground { get; private set; } = Color.FromArgb(50, 50, 56);
        public Color Accent { get; private set; } = Color.FromArgb(58, 134, 255);

        public static MbTheme Default => new MbTheme();

        public static MbTheme FromMusicBee(MusicBeeApiInterface api)
        {
            var theme = new MbTheme();
            try
            {
                if (api.Setting_GetSkinElementColour == null) return theme;

                // Read ONLY the main panel background from the skin. Other
                // elements (SkinInputControl, SkinSubPanel) are designed for
                // different visual roles in the skin (e.g. the white search
                // box at the top of MusicBee, the light list rows of certain
                // skins) and grabbing their colours produces an inconsistent
                // palette inside our chat panel. Derive every other surface
                // colour from the main background instead -- shaded lighter
                // or darker depending on whether the skin is dark or light.
                var bg = ReadColor(api, SkinElement.SkinSubPanel, ElementState.ElementStateDefault, ElementComponent.ComponentBackground)
                      ?? ReadColor(api, SkinElement.SkinElementPanel, ElementState.ElementStateDefault, ElementComponent.ComponentBackground);

                if (!bg.HasValue) return theme; // keep dark defaults

                theme.Background = bg.Value;
                bool isDark = Luma(bg.Value) < 0.5;

                // Derive surfaces with small shifts so the chat panel reads as
                // a single coherent palette regardless of the skin in use.
                theme.BackgroundAlt   = Shift(bg.Value, isDark ? +0.05f : -0.05f);
                theme.InputBackground = Shift(bg.Value, isDark ? +0.10f : -0.06f);
                theme.ButtonBackground = Shift(bg.Value, isDark ? +0.15f : -0.10f);
                theme.Border          = Shift(bg.Value, isDark ? +0.22f : -0.18f);

                theme.Foreground = isDark
                    ? Color.FromArgb(238, 238, 238)
                    : Color.FromArgb(28, 28, 30);
                theme.ForegroundDim = Mix(theme.Foreground, theme.Background, 0.55f);
            }
            catch
            {
                // Leave defaults if anything blew up.
            }
            return theme;
        }

        private static double Luma(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

        // Shift a colour toward white (positive amt) or black (negative amt).
        // amt is in the 0..1 range. Used to derive coordinated surface colours
        // from a single base background.
        private static Color Shift(Color c, float amt)
        {
            if (amt >= 0)
            {
                int R = (int)(c.R + (255 - c.R) * amt);
                int G = (int)(c.G + (255 - c.G) * amt);
                int B = (int)(c.B + (255 - c.B) * amt);
                return Color.FromArgb(255, Clamp(R), Clamp(G), Clamp(B));
            }
            else
            {
                float t = -amt;
                int R = (int)(c.R * (1f - t));
                int G = (int)(c.G * (1f - t));
                int B = (int)(c.B * (1f - t));
                return Color.FromArgb(255, Clamp(R), Clamp(G), Clamp(B));
            }
        }

        private static int Clamp(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);

        private static Color? ReadColor(MusicBeeApiInterface api, SkinElement element, ElementState state, ElementComponent component)
        {
            try
            {
                var raw = api.Setting_GetSkinElementColour(element, state, component);
                // MusicBee returns the colour as a 32-bit value in the same
                // layout as Color.ToArgb() -- 0xAARRGGBB with R in bits 16-23,
                // G in 8-15, B in 0-7. The alpha byte is often 0, so OR in a
                // full alpha to make sure the result isn't transparent.
                if (raw == 0 || raw == -1) return null;
                unchecked
                {
                    return Color.FromArgb(raw | (int)0xFF000000);
                }
            }
            catch { return null; }
        }

        private static Color Mix(Color a, Color b, float t)
        {
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            int R = (int)(a.R * (1f - t) + b.R * t);
            int G = (int)(a.G * (1f - t) + b.G * t);
            int B = (int)(a.B * (1f - t) + b.B * t);
            return Color.FromArgb(255, R, G, B);
        }
    }
}
