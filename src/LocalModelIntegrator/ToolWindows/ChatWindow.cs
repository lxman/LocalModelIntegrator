using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using System.Runtime.InteropServices;

namespace LocalModelIntegrator.ToolWindows
{
    [Guid("B2C3D4E5-F6A7-8B9C-0D1E-2F3A4B5C6D7E")]
    public sealed class ChatWindow : ToolWindowPane
    {
        public ChatWindow() : base(null)
        {
            Caption = "Local Model Integrator";
            BitmapImageMoniker = KnownMonikers.CommentGroup;
            Content = new ChatWindowControl();
        }
    }
}
