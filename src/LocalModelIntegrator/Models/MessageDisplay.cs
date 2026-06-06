using System.ComponentModel;

namespace LocalModelIntegrator.Models
{
    /// <summary>
    /// Wrapper for displaying messages in the chat list.
    /// Implements INotifyPropertyChanged so streamed responses update the UI live
    /// as tokens arrive.
    /// </summary>
    public class MessageDisplay : INotifyPropertyChanged
    {
        private string _content;
        private string _header = "Thinking";
        private bool _isActive;

        public string Role { get; set; }

        /// <summary>True while a thinking bubble is still streaming - drives the "working" border pulse.</summary>
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive == value) return;
                _isActive = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
            }
        }

        public string Content
        {
            get => _content;
            set
            {
                if (_content == value) return;
                _content = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Content)));
            }
        }

        /// <summary>Collapsed-state caption for the thinking bubble (a peek of the reasoning).</summary>
        public string Header
        {
            get => _header;
            set
            {
                if (_header == value) return;
                _header = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Header)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
