using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
namespace OllamaDesktop.App.Controls;

// Original, shared 24-unit outline drawings. No platform font glyph dependency.
public sealed class GlyphIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty=DependencyProperty.Register(nameof(Kind),typeof(string),typeof(GlyphIcon),new FrameworkPropertyMetadata("file",FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get=>(string)GetValue(KindProperty);set=>SetValue(KindProperty,value); }
    private static readonly Dictionary<string,string> Shapes=new() {
        ["menu"]="M4,6H20 M4,12H20 M4,18H20",["plus"]="M12,4V20 M4,12H20",
        ["search"]="M16,16L21,21 M18,10A8,8 0 1 1 2,10A8,8 0 1 1 18,10",
        ["file"]="M6,2H14L20,8V22H6Z M14,2V8H20 M9,12H17 M9,16H17",
        ["folder"]="M3,5H10L12,8H21V20H3Z",
        ["settings"]="M9,3H15L16,6L19,5L22,10L20,12L22,15L19,20L16,19L15,22H9L8,19L5,20L2,15L4,12L2,10L5,5L8,6Z M16,12A4,4 0 1 1 8,12A4,4 0 1 1 16,12",
        ["refresh"]="M20,5V11H14 M20,11A8,8 0 1 0 19,17",
        ["diagnostics"]="M4,20V13 M10,20V4 M16,20V9 M22,20H2",
        ["info"]="M22,12A10,10 0 1 1 2,12A10,10 0 1 1 22,12 M12,11V17 M12,6V7",
        ["send"]="M3,3L22,12L3,21L6,12Z M6,12H22",
        ["stop"]="M6,6H18V18H6Z",
        ["clip"]="M8,14L15,7A3,3 0 0 1 19,11L10,20A5,5 0 0 1 3,13L14,2",
        ["image"]="M3,3H21V21H3Z M3,17L9,11L15,17L18,14L21,17 M17,7A1,1 0 1 1 15,7A1,1 0 1 1 17,7",
        ["copy"]="M8,8H21V21H8Z M16,8V3H3V16H8",
        ["down"]="M6,9L12,15L18,9",["up"]="M6,15L12,9L18,15",
        ["download"]="M12,3V16 M6,10L12,16L18,10 M3,17V21H21V17",
        ["more"]="M4,12H5 M11,12H12 M18,12H19",
        ["close"]="M5,5L19,19 M19,5L5,19",
        ["play"]="M7,3L21,12L7,21Z",
        ["audio"]="M9,3H15V13A3,3 0 0 1 9,13Z M5,11V13A7,7 0 0 0 19,13V11 M12,20V23"
    };
    static GlyphIcon()=>TextElement.ForegroundProperty.AddOwner(typeof(GlyphIcon),new FrameworkPropertyMetadata(Brushes.Gray,FrameworkPropertyMetadataOptions.Inherits|FrameworkPropertyMetadataOptions.AffectsRender));
    public GlyphIcon(){Width=20;Height=20;IsHitTestVisible=false;}
    protected override void OnRender(DrawingContext dc){base.OnRender(dc);dc.PushTransform(new ScaleTransform(ActualWidth/24,ActualHeight/24));var pen=new Pen(TextElement.GetForeground(this),1.7){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round};dc.DrawGeometry(null,pen,Geometry.Parse(Shapes.GetValueOrDefault(Kind,Shapes["file"])));dc.Pop();}
}
