using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using LocalModelIntegrator.Services;

namespace LocalModelIntegrator.Options
{
    /// <summary>
    /// Custom UI for the options page: hosts the standard property grid (so every setting still
    /// renders automatically from its attributes) and adds a "Test Connection" button that probes
    /// the currently-entered endpoint and shows the capability report inline.
    /// </summary>
    internal sealed class GeneralOptionsControl : UserControl
    {
        private readonly GeneralOptions _options;
        private readonly PropertyGrid _grid;
        private readonly Button _testButton;
        private readonly TextBox _resultBox;

        public GeneralOptionsControl(GeneralOptions options)
        {
            _options = options;

            _grid = new PropertyGrid
            {
                Dock = DockStyle.Fill,
                SelectedObject = _options,        // edits write straight to the page that gets persisted
                PropertySort = PropertySort.Categorized,
                ToolbarVisible = true,
                HelpVisible = true
            };

            _testButton = new Button
            {
                Text = "Test Connection",
                AutoSize = true,
                Anchor = AnchorStyles.Left
            };
            _testButton.Click += TestButton_Click;

            _resultBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 8.25f),
                Text = "Click \"Test Connection\" to probe the endpoint configured above."
            };

            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            bottom.Controls.Add(_testButton, 0, 0);
            bottom.Controls.Add(_resultBox, 0, 1);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 170f));
            layout.Controls.Add(_grid, 0, 0);
            layout.Controls.Add(bottom, 0, 1);

            Dock = DockStyle.Fill;
            Controls.Add(layout);
        }

        /// <summary>Re-reads the bound options into the grid (settings may have been reloaded from storage).</summary>
        public void RefreshGrid() => _grid.Refresh();

        private async void TestButton_Click(object sender, EventArgs e)
        {
            _testButton.Enabled = false;
            _resultBox.Text = "Testing connection to " + _options.ApiUrl + " …";
            try
            {
                ModelCapabilities caps = await CapabilityProbe.ProbeAsync(_options, CancellationToken.None);
                _resultBox.Text = caps.Report;
            }
            catch (Exception ex)
            {
                _resultBox.Text = "Test failed: " + ex.Message;
            }
            finally
            {
                _testButton.Enabled = true;
            }
        }
    }
}
