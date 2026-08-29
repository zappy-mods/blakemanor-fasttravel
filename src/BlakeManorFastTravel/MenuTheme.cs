using UnityEngine;

namespace BlakeManorFastTravel
{
    // Lazily-built IMGUI styling that echoes the game's own comic-book / gothic-Victorian
    // presentation - modeled directly on the game's in-fiction "Observations" popup:
    // a near-black panel, a thin gold frame, warm serif type, and a wine-red button accent.
    // GUIStyle/Texture2D can only safely be constructed from inside OnGUI, so this is built
    // once on first use and cached for the life of the process.
    internal static class MenuTheme
    {
        public static bool Built { get; private set; }

        public static GUIStyle Panel;
        public static GUIStyle Title;
        public static GUIStyle Subtitle;
        public static GUIStyle Body;
        public static GUIStyle Status;
        public static GUIStyle DestinationButton;
        public static GUIStyle CloseButton;
        public static GUIStyle ScrollBackground;
        public static Texture2D Overlay;
        public static Texture2D Rule;

        // Palette pulled from the game's own screens: near-black eggplot panels, a muted
        // gold frame/heading colour, warm cream body text, and a wine-red button fill.
        private static readonly Color PanelFill = new Color32(0x1b, 0x10, 0x16, 0xf2);
        private static readonly Color PanelBorder = new Color32(0xca, 0xa2, 0x5a, 0xff);
        private static readonly Color Gold = new Color32(0xe8, 0xb0, 0x4b, 0xff);
        private static readonly Color Cream = new Color32(0xe9, 0xe0, 0xd2, 0xff);
        private static readonly Color CreamDim = new Color32(0xb9, 0xae, 0x9c, 0xff);
        private static readonly Color WineFill = new Color32(0x3a, 0x1f, 0x28, 0xff);
        private static readonly Color WineFillHover = new Color32(0x57, 0x2a, 0x39, 0xff);
        private static readonly Color ScrollFill = new Color32(0x14, 0x0c, 0x10, 0xd9);
        public static readonly Color Alert = new Color32(0xc9, 0x6b, 0x4e, 0xff);

        // Font sizes as authored at the window's default size - ApplyScale multiplies
        // these, it never mutates them, so repeated resizes don't compound rounding error.
        private const int TitleBaseSize = 24;
        private const int SubtitleBaseSize = 13;
        private const int BodyBaseSize = 14;
        private const int DestinationButtonBaseSize = 15;
        private const int CloseButtonBaseSize = 13;

        public static void EnsureBuilt()
        {
            if (Built)
            {
                return;
            }
            Built = true;

            Font serif = TryLoadSerifFont();

            Overlay = SolidTexture(new Color(0f, 0f, 0f, 0.72f));
            Rule = SolidTexture(Gold);

            Panel = new GUIStyle(GUI.skin.box)
            {
                border = new RectOffset(16, 16, 16, 16),
                padding = new RectOffset(0, 0, 0, 0)
            };
            Panel.normal.background = RoundedRect(64, 14, PanelFill, PanelBorder, 2);

            ScrollBackground = new GUIStyle(GUI.skin.box)
            {
                border = new RectOffset(12, 12, 12, 12)
            };
            ScrollBackground.normal.background = RoundedRect(64, 10, ScrollFill, Color.clear, 0);

            Title = new GUIStyle(GUI.skin.label)
            {
                font = serif,
                fontSize = 24,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            Title.normal.textColor = Gold;

            Subtitle = new GUIStyle(GUI.skin.label)
            {
                font = serif,
                fontSize = 13,
                fontStyle = FontStyle.Italic,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true
            };
            Subtitle.normal.textColor = CreamDim;

            Body = new GUIStyle(GUI.skin.label)
            {
                font = serif,
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true
            };
            Body.normal.textColor = CreamDim;

            Status = new GUIStyle(Body);
            Status.normal.textColor = Alert;

            DestinationButton = new GUIStyle(GUI.skin.button)
            {
                font = serif,
                fontSize = 15,
                alignment = TextAnchor.MiddleCenter,
                border = new RectOffset(10, 10, 10, 10),
                margin = new RectOffset(4, 4, 4, 4)
            };
            DestinationButton.normal.textColor = Cream;
            DestinationButton.hover.textColor = Gold;
            DestinationButton.active.textColor = Gold;
            DestinationButton.normal.background = RoundedRect(48, 8, WineFill, PanelBorder, 1);
            DestinationButton.hover.background = RoundedRect(48, 8, WineFillHover, Gold, 1);
            DestinationButton.active.background = DestinationButton.hover.background;

            CloseButton = new GUIStyle(DestinationButton)
            {
                fontSize = 13
            };
            CloseButton.normal.textColor = CreamDim;
            CloseButton.hover.textColor = Gold;
            CloseButton.normal.background = RoundedRect(48, 8, Color.clear, PanelBorder, 1);
            CloseButton.hover.background = RoundedRect(48, 8, new Color(1f, 1f, 1f, 0.05f), Gold, 1);
            CloseButton.active.background = CloseButton.hover.background;
        }

        // Re-applies font sizes each frame off the window's current/default size ratio, so
        // text (and the buttons sized to fit it) grow and shrink smoothly as the player
        // drags the resize grip. GUIStyle.fontSize is cheap to mutate - no texture rebuilds.
        public static void ApplyScale(float scale)
        {
            Title.fontSize = Mathf.RoundToInt(TitleBaseSize * scale);
            Subtitle.fontSize = Mathf.RoundToInt(SubtitleBaseSize * scale);
            Body.fontSize = Mathf.RoundToInt(BodyBaseSize * scale);
            Status.fontSize = Mathf.RoundToInt(BodyBaseSize * scale);
            DestinationButton.fontSize = Mathf.RoundToInt(DestinationButtonBaseSize * scale);
            CloseButton.fontSize = Mathf.RoundToInt(CloseButtonBaseSize * scale);
        }

        private static Font TryLoadSerifFont()
        {
            try
            {
                // These match the elegant gothic-serif look of the game's own logo/UI type;
                // whichever is installed first wins, and if none are, GUIStyle just falls
                // back to Unity's default font (font left null).
                return Font.CreateDynamicFontFromOSFont(
                    new[] { "Georgia", "Book Antiqua", "Palatino Linotype", "Times New Roman" }, 16);
            }
            catch
            {
                return null;
            }
        }

        private static Texture2D SolidTexture(Color color)
        {
            Texture2D tex = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        // Builds a small square texture holding a rounded rectangle (with an optional inset
        // border), meant to be stretched via GUIStyle.border as a 9-slice rather than scaled
        // directly - that's what keeps the corners crisp at any panel/button size.
        private static Texture2D RoundedRect(int size, float radius, Color fill, Color border, float borderThickness)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            bool hasBorder = borderThickness > 0f && border.a > 0f;
            Color[] pixels = new Color[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = RoundedBoxSdf(x + 0.5f, y + 0.5f, size, size, radius);
                    float coverage = Mathf.Clamp01(0.5f - dist);

                    Color c;
                    if (coverage <= 0f)
                    {
                        c = Color.clear;
                    }
                    else
                    {
                        c = (hasBorder && dist > -borderThickness) ? border : fill;
                        c.a *= coverage;
                    }
                    pixels[y * size + x] = c;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        // Exact 2D signed-distance field for a rounded box: negative inside, positive
        // outside, zero at the boundary (in pixel units).
        private static float RoundedBoxSdf(float px, float py, float w, float h, float radius)
        {
            float halfW = w / 2f;
            float halfH = h / 2f;
            float dx = Mathf.Abs(px - halfW) - (halfW - radius);
            float dy = Mathf.Abs(py - halfH) - (halfH - radius);
            float outsideX = Mathf.Max(dx, 0f);
            float outsideY = Mathf.Max(dy, 0f);
            return Mathf.Sqrt(outsideX * outsideX + outsideY * outsideY) + Mathf.Min(Mathf.Max(dx, dy), 0f) - radius;
        }
    }
}
