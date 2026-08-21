using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AudioConvert.Services;

namespace AudioConvert
{
    public partial class TrialMembershipDialog : Window
    {
        public TrialMembershipDialog()
        {
            InitializeComponent();
        }

        public bool ClaimRequested { get; private set; }

        private void ClaimButton_Click(object sender, RoutedEventArgs e)
        {
            ClaimRequested = true;
            DialogResult = true;
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    public sealed class TrialMembershipDialogPresenter : ITrialMembershipPromptPresenter
    {
        private readonly Func<Window?> _ownerProvider;

        public TrialMembershipDialogPresenter(Func<Window?> ownerProvider)
        {
            _ownerProvider = ownerProvider ?? throw new ArgumentNullException(nameof(ownerProvider));
        }

        public Task<TrialMembershipPromptResult> ShowAsync(TrialMembershipPromptTrigger trigger)
        {
            var dialog = new TrialMembershipDialog();
            Window? owner = _ownerProvider();
            if (owner is not null && owner.IsVisible)
            {
                dialog.Owner = owner;
            }

            using (DialogOwnerDimming.Apply(owner))
            {
                dialog.ShowDialog();
                return Task.FromResult(
                    dialog.ClaimRequested
                        ? TrialMembershipPromptResult.Claimed
                        : TrialMembershipPromptResult.Closed);
            }
        }
    }
}
