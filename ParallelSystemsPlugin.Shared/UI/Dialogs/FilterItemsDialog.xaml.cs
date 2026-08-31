using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParallelSystemsPlugin.Commands;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;

namespace ParallelSystemPlugin.UI
{
    public partial class FilterItemsDialog : Window
    {
        private readonly FilterItemsExternalEventHandler _handler;
        private readonly ExternalEvent _externalEvent;
        private bool _bulkUpdate;

        public IList<FilterItemGroupModel> Groups { get; private set; }

        internal FilterItemsDialog(
            string viewName,
            IList<FilterItemGroupModel> groups,
            FilterItemsExternalEventHandler handler,
            ExternalEvent externalEvent)
        {
            InitializeComponent();

            Groups = groups ?? new List<FilterItemGroupModel>();
            _handler = handler;
            _externalEvent = externalEvent;

            Title = "Filter Items - " + (viewName ?? "Active View");
            PART_ViewName.Text = "Active View: " + (viewName ?? "");
            Icon = AppDialog.LoadWindowIcon();
            DataContext = this;

            foreach (FilterItemModel item in Groups.SelectMany(x => x.Items))
                item.PropertyChanged += OnItemPropertyChanged;

            UpdateSummary();
        }

        public void ShowModeless(IntPtr owner)
        {
            if (owner != IntPtr.Zero)
                new WindowInteropHelper(this) { Owner = owner };

            Show();
            Activate();
        }

        private void OnItemPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_bulkUpdate || e.PropertyName != "IsVisible")
                return;

            ApplyVisibility();
        }

        private void OnCheckAllClick(object sender, RoutedEventArgs e)
        {
            SetAll(true);
        }

        private void OnUncheckAllClick(object sender, RoutedEventArgs e)
        {
            SetAll(false);
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void SetAll(bool visible)
        {
            _bulkUpdate = true;
            try
            {
                foreach (FilterItemModel item in Groups.SelectMany(x => x.Items))
                    item.IsVisible = visible;
            }
            finally
            {
                _bulkUpdate = false;
            }

            ApplyVisibility();
        }

        private void ApplyVisibility()
        {
            List<ElementId> hiddenIds = Groups
                .SelectMany(x => x.Items)
                .Where(x => !x.IsVisible)
                .SelectMany(x => x.ElementIds)
                .Distinct()
                .ToList();

            _handler.SetHiddenIds(hiddenIds);

            try
            {
                _externalEvent.Raise();
            }
            catch (Exception ex)
            {
                AppDialog.Error("Filter Items", "Unable to queue the visibility update.\n\n" + ex.Message);
            }

            UpdateSummary();
        }

        private void UpdateSummary()
        {
            List<FilterItemModel> items = Groups.SelectMany(x => x.Items).ToList();
            int totalTypes = items.Count;
            int totalQty = items.Sum(x => x.Quantity);
            int visibleTypes = items.Count(x => x.IsVisible);
            int visibleQty = items.Where(x => x.IsVisible).Sum(x => x.Quantity);

            PART_Summary.Text = string.Format(
                "Visible: {0:N0} / {1:N0} item types   |   {2:N0} / {3:N0} components",
                visibleTypes,
                totalTypes,
                visibleQty,
                totalQty);
        }
    }

    public sealed class FilterItemGroupModel
    {
        public FilterItemGroupModel(string name, IList<FilterItemModel> items)
        {
            Name = name ?? "OTHER";
            Items = items ?? new List<FilterItemModel>();
        }

        public string Name { get; private set; }
        public IList<FilterItemModel> Items { get; private set; }
        public int Quantity { get { return Items.Sum(x => x.Quantity); } }
    }

    public sealed class FilterItemModel : INotifyPropertyChanged
    {
        private bool _isVisible = true;

        public FilterItemModel(
            string groupName,
            string displayName,
            IList<ElementId> elementIds)
        {
            GroupName = groupName ?? "OTHER";
            DisplayName = displayName ?? "Component";
            ElementIds = elementIds ?? new List<ElementId>();
        }

        public string GroupName { get; private set; }
        public string DisplayName { get; private set; }
        public IList<ElementId> ElementIds { get; private set; }
        public int Quantity { get { return ElementIds.Count; } }

        public bool IsVisible
        {
            get { return _isVisible; }
            set
            {
                if (_isVisible == value)
                    return;

                _isVisible = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
