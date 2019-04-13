using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace mtga_log_client
{
    /// <summary>
    /// Interaction logic for Window1.xaml
    /// </summary>
    public partial class ExitConfirmation : Window
    {
        public enum ExitState
        {
            CANCEL,
            MINIMIZE,
            EXIT
        }

        private bool remember = false;
        private ExitState exitState = ExitState.CANCEL;

        public ExitConfirmation()
        {
            InitializeComponent();
        }

        public ExitState GetExitState()
        {
            return this.exitState;
        }

        public bool GetRemember()
        {
            return this.remember;
        }

        private void SetStateAndExit(ExitState exitState)
        {
            this.remember = RememberCheckbox.IsChecked.GetValueOrDefault(false);
            this.exitState = exitState;
            Close();
        }

        private void Cancel_onClick(object sender, EventArgs e)
        {
            SetStateAndExit(ExitState.CANCEL);
        }

        private void Minimize_onClick(object sender, EventArgs e)
        {
            SetStateAndExit(ExitState.MINIMIZE);
        }

        private void Exit_onClick(object sender, EventArgs e)
        {
            SetStateAndExit(ExitState.EXIT);
        }
    }
}
