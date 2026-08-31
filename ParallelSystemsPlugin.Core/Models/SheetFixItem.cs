namespace ParallelSystemsPlugin.Models
{
    public class SheetFixItem
    {
        public long SheetId { get; set; }
        public string AssemblyName { get; set; }
        public string OldSheetNumber { get; set; }
        public string NewSheetNumber { get; set; }
        public string SheetName { get; set; }
        public string TabPreview { get; set; }
    }
}
