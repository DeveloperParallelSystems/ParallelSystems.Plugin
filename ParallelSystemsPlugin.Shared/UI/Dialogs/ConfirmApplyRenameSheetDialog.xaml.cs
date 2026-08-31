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
using System.Windows.Navigation;
using System.Windows.Shapes;
using ParallelSystemsPlugin.Models;

namespace ParallelSystemsPlugin.UI.Dialogs
{
    /// <summary>
    /// Interaction logic for ConfirmApplyRenameSheetDialog.xaml
    /// </summary>
    public partial class ConfirmApplyRenameSheetDialog : Window
    {
        public List<SheetFixItem> Items { get; private set; }
        public List<SheetFixItem> SelectedItem { get; private set; }
        public bool ExecuteFix { get; private set; } = false;
        public ConfirmApplyRenameSheetDialog(List<SheetFixItem> items)
        {
            InitializeComponent();

            Items = items;
            FixGrid.ItemsSource = Items;
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            ExecuteFix = true;
            var selectedItems = FixGrid.SelectedItems.Cast<SheetFixItem>().ToList();
            SelectedItem = selectedItems;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            ExecuteFix = false;
            Close();
        }

        private void ApplyFixToAll_Click(object sender, RoutedEventArgs e)
        {
            ExecuteFix = true;
            SelectedItem = Items;
            Close();
        }
    }
}
