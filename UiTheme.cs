using ImGuiNET;
using SN = System.Numerics;

namespace GravitySim;

/// <summary>
/// Semantic color tokens + global ImGui style for the deep-space look.
/// Single source of truth: no raw hex anywhere else in the UI code.
/// </summary>
public static class UiTheme
{
    public static readonly SN.Vector4 Background    = Rgb(0x0D1220);
    public static readonly SN.Vector4 Surface       = Rgb(0x161D2E);
    public static readonly SN.Vector4 SurfaceHover  = Rgb(0x1E2740);
    public static readonly SN.Vector4 SurfaceActive = Rgb(0x263252);
    public static readonly SN.Vector4 Accent        = Rgb(0x4CC3FF);
    public static readonly SN.Vector4 AccentDim     = Rgb(0x2A6E96);
    public static readonly SN.Vector4 Text          = Rgb(0xE6EDF7);
    public static readonly SN.Vector4 TextMuted     = Rgb(0x8A94A8);
    public static readonly SN.Vector4 Border        = Rgb(0x2A3350);
    public static readonly SN.Vector4 Danger        = Rgb(0xEF4444);

    public static void Apply()
    {
        var style = ImGui.GetStyle();

        style.WindowRounding = 0f;      // sidebar sits flush against the edge
        style.FrameRounding = 4f;
        style.GrabRounding = 4f;
        style.TabRounding = 4f;
        style.PopupRounding = 4f;
        style.ScrollbarRounding = 6f;
        style.WindowPadding = new SN.Vector2(10f, 10f);
        style.FramePadding = new SN.Vector2(8f, 4f);
        style.ItemSpacing = new SN.Vector2(8f, 6f);
        style.ScrollbarSize = 12f;
        style.WindowBorderSize = 0f;

        var c = style.Colors;
        c[(int)ImGuiCol.WindowBg]             = Background;
        c[(int)ImGuiCol.ChildBg]              = Background;
        c[(int)ImGuiCol.PopupBg]              = Surface;
        c[(int)ImGuiCol.Border]               = Border;
        c[(int)ImGuiCol.FrameBg]              = Surface;
        c[(int)ImGuiCol.FrameBgHovered]       = SurfaceHover;
        c[(int)ImGuiCol.FrameBgActive]        = SurfaceActive;
        c[(int)ImGuiCol.TitleBg]              = Background;
        c[(int)ImGuiCol.TitleBgActive]        = Background;
        c[(int)ImGuiCol.Header]               = Surface;
        c[(int)ImGuiCol.HeaderHovered]        = SurfaceHover;
        c[(int)ImGuiCol.HeaderActive]         = SurfaceActive;
        c[(int)ImGuiCol.Button]               = Surface;
        c[(int)ImGuiCol.ButtonHovered]        = SurfaceHover;
        c[(int)ImGuiCol.ButtonActive]         = AccentDim;
        c[(int)ImGuiCol.SliderGrab]           = Accent;
        c[(int)ImGuiCol.SliderGrabActive]     = Accent;
        c[(int)ImGuiCol.CheckMark]            = Accent;
        c[(int)ImGuiCol.Tab]                  = Surface;
        c[(int)ImGuiCol.TabHovered]           = SurfaceHover;
        c[(int)ImGuiCol.TabSelected]          = SurfaceActive;
        c[(int)ImGuiCol.Separator]            = Border;
        c[(int)ImGuiCol.SeparatorHovered]     = AccentDim;
        c[(int)ImGuiCol.Text]                 = Text;
        c[(int)ImGuiCol.TextDisabled]         = TextMuted;
        c[(int)ImGuiCol.PlotLines]            = Accent;
        c[(int)ImGuiCol.PlotHistogram]        = Accent;
        c[(int)ImGuiCol.ScrollbarBg]          = Background;
        c[(int)ImGuiCol.ScrollbarGrab]        = Surface;
        c[(int)ImGuiCol.ScrollbarGrabHovered] = SurfaceHover;
        c[(int)ImGuiCol.ScrollbarGrabActive]  = SurfaceActive;
        c[(int)ImGuiCol.ResizeGrip]           = Surface;
    }

    private static SN.Vector4 Rgb(uint hex, float a = 1f)
        => new(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, a);
}
