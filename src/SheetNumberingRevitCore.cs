using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using SheetNumberingRevit.Commands.Models;

// FIX MODELESS TRANSACTION: Alias for cleaner code
using Models = SheetNumberingRevit.Commands.Models;

// =============================================================================
// SHEETNUMBERINGREVIT CORE - All C# code consolidated into one file
// =============================================================================

namespace SheetNumberingRevit
{
    // ==========================================================================
    // LOGGING SERVICE
    // ==========================================================================
    public static class LoggingService
    {
        private static readonly string LogDirectory;
        private static readonly string LogFilePath;
        private static readonly object LockObj = new();

        static LoggingService()
        {
            LogDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SheetNumberingRevit",
                "logs"
            );
            if (!Directory.Exists(LogDirectory))
                Directory.CreateDirectory(LogDirectory);
            LogFilePath = Path.Combine(LogDirectory, $"log_{DateTime.Now:yyyyMMdd}.txt");
        }

        public static void Log(string message, LogLevel level = LogLevel.Info)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var logEntry = $"[{timestamp}] [{level}] {message}";
            lock (LockObj) { try { File.AppendAllText(LogFilePath, logEntry + Environment.NewLine); } catch { } }
        }

        public static void LogInfo(string message) => Log(message, LogLevel.Info);
        public static void LogWarning(string message) => Log(message, LogLevel.Warning);
        public static void LogError(string message) => Log(message, LogLevel.Error);
        public static void LogDebug(string message) => Log(message, LogLevel.Debug);
        public static string GetLogDirectory() => LogDirectory;
    }

    public enum LogLevel { Debug, Info, Warning, Error }

    // ==========================================================================
    // EXTERNAL EVENT HANDLER - FIX MODELESS TRANSACTION
    // ==========================================================================
    /// <summary>
    /// IExternalEventHandler to execute Sheet Number updates in Revit API context.
    /// This solves the Modeless WPF Window transaction issue by running the
    /// transaction on Revit's main thread instead of the UI thread.
    /// </summary>
    public class SheetNumberUpdateHandler : IExternalEventHandler
    {
        // FIX MODELESS TRANSACTION: Data container to pass from ViewModel to Handler
        private class UpdateData
        {
            public List<Models.SheetInfo> SheetsToUpdate { get; set; } = new();
            public Models.NumberingOptions Options { get; set; } = new();
            public Action<Models.NumberingResult>? OnComplete { get; set; }
        }

        private UpdateData? _pendingData;
        private readonly object _lockObj = new();

        // FIX MODELESS TRANSACTION: Called from ViewModel before Raise()
        public void SetUpdateData(IEnumerable<Models.SheetInfo> sheets, Models.NumberingOptions options, Action<Models.NumberingResult>? onComplete)
        {
            lock (_lockObj)
            {
                _pendingData = new UpdateData
                {
                    SheetsToUpdate = sheets.ToList(),
                    Options = options,
                    OnComplete = onComplete
                };
            }
        }

        public string GetName() => "SheetNumberUpdateHandler";

        public void Execute(UIApplication app)
        {
            UpdateData? data;
            lock (_lockObj)
            {
                data = _pendingData;
                _pendingData = null;
            }

            if (data == null || data.SheetsToUpdate.Count == 0)
            {
                LoggingService.LogWarning("SheetNumberUpdateHandler: No data to process");
                return;
            }

            var result = new Models.NumberingResult();
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
            {
                result.Errors.Add("Không thể truy cập Revit Document");
                data.OnComplete?.Invoke(result);
                return;
            }

            LoggingService.LogInfo($"SheetNumberUpdateHandler: Bắt đầu cập nhật {data.SheetsToUpdate.Count} sheets...");

            var selectedSheetsWithChanges = data.SheetsToUpdate
                .Where(s => s.IsSelected && !string.IsNullOrEmpty(s.NewSheetNumber) && s.HasChanges)
                .ToList();

            if (selectedSheetsWithChanges.Count == 0)
            {
                LoggingService.LogWarning("SheetNumberUpdateHandler: Không có sheet nào cần thay đổi.");
                result.Success = true;
                data.OnComplete?.Invoke(result);
                return;
            }

            var transactionGroup = new TransactionGroup(doc, "Renumber Sheets Group");
            try
            {
                transactionGroup.Start();

                // Phase 1: Assign Temporary Numbers
                using (var transaction = new Transaction(doc, "Phase 1 - Assign Temporary Numbers"))
                {
                    transaction.Start();
                    int tempIndex = 0;
                    foreach (var sheet in selectedSheetsWithChanges)
                    {
                        var tempNumber = $"__TEMP_{Guid.NewGuid():N}_{tempIndex}";
                        var viewSheet = doc.GetElement(sheet.ElementId) as ViewSheet;
                        if (viewSheet != null)
                        {
                            viewSheet.SheetNumber = tempNumber;
                            LoggingService.LogDebug($"Handler: Gán số tạm {sheet.CurrentSheetNumber} -> {tempNumber}");
                        }
                        tempIndex++;
                    }
                    transaction.Commit();
                }

                // Phase 2: Assign Final Numbers
                using (var transaction = new Transaction(doc, "Phase 2 - Assign Final Numbers"))
                {
                    transaction.Start();
                    foreach (var sheet in selectedSheetsWithChanges)
                    {
                        var viewSheet = doc.GetElement(sheet.ElementId) as ViewSheet;
                        if (viewSheet != null)
                        {
                            try
                            {
                                viewSheet.SheetNumber = sheet.NewSheetNumber;
                                sheet.CurrentSheetNumber = sheet.NewSheetNumber;
                                sheet.NewSheetNumber = string.Empty;
                                result.SheetsUpdated++;
                                LoggingService.LogDebug($"Handler: Gán số cuối {sheet.NewSheetNumber}");
                            }
                            catch (Exception ex)
                            {
                                var errorMsg = $"Lỗi khi gán số cho sheet '{sheet.SheetName}': {ex.Message}";
                                result.Errors.Add(errorMsg);
                                LoggingService.LogError(errorMsg);
                            }
                        }
                    }
                    transaction.Commit();
                }

                transactionGroup.Assimilate();
                result.Success = result.Errors.Count == 0;
                LoggingService.LogInfo($"Handler: Hoàn thành. Đã cập nhật {result.SheetsUpdated} sheet.");
            }
            catch (Exception ex)
            {
                LoggingService.LogError($"Handler: Lỗi transaction: {ex.Message}");
                if (transactionGroup.HasStarted()) { transactionGroup.RollBack(); }
                result.Success = false;
                result.Errors.Add($"Lỗi nghiêm trọng: {ex.Message}");
            }

            // FIX MODELESS TRANSACTION: Invoke callback on UI thread
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                data.OnComplete?.Invoke(result);
            });
        }

        public Result Raise()
        {
            // This method is called externally to trigger the event
            return Result.Succeeded;
        }
    }

    // ==========================================================================
    // HELPERS - CONVERTERS
    // ==========================================================================
    namespace Commands.Helpers
    {
        using System.Globalization;
        using System.Text.RegularExpressions;
        using System.Windows;
        using System.Windows.Data;
        using Visibility = System.Windows.Visibility;

        public class NaturalSortComparer : IComparer<string>, System.Collections.IComparer
        {
            private static readonly Regex _numberPattern = new(@"\d+", RegexOptions.Compiled);

            public int Compare(string? x, string? y)
            {
                if (x == null && y == null) return 0;
                if (x == null) return -1;
                if (y == null) return 1;
                var partsX = SplitIntoParts(x);
                var partsY = SplitIntoParts(y);
                int minLen = Math.Min(partsX.Count, partsY.Count);
                for (int i = 0; i < minLen; i++)
                {
                    int cmp = partsX[i].NumericValue.HasValue && partsY[i].NumericValue.HasValue
                        ? partsX[i].NumericValue.Value.CompareTo(partsY[i].NumericValue.Value)
                        : string.Compare(partsX[i].Text, partsY[i].Text, StringComparison.Ordinal);
                    if (cmp != 0) return cmp;
                }
                return partsX.Count.CompareTo(partsY.Count);
            }

            int System.Collections.IComparer.Compare(object? x, object? y) => Compare(x as string, y as string);

            private static List<Part> SplitIntoParts(string s)
            {
                var parts = new List<Part>();
                int last = 0;
                foreach (Match m in _numberPattern.Matches(s))
                {
                    if (m.Index > last) parts.Add(new Part(s[last..m.Index], null));
                    parts.Add(new Part(s.Substring(m.Index, m.Length), int.Parse(m.Value)));
                    last = m.Index + m.Length;
                }
                if (last < s.Length) parts.Add(new Part(s[last..], null));
                return parts;
            }

            private struct Part
            {
                public string Text { get; }
                public int? NumericValue { get; }
                public Part(string text, int? num) { Text = text; NumericValue = num; }
            }
        }

        public class BoolToVisibilityConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                if (value is bool boolValue)
                {
                    var invert = parameter?.ToString()?.ToLower() == "invert";
                    return (invert ? !boolValue : boolValue) ? Visibility.Visible : Visibility.Collapsed;
                }
                return Visibility.Collapsed;
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                if (value is Visibility visibility)
                {
                    var invert = parameter?.ToString()?.ToLower() == "invert";
                    var result = visibility == Visibility.Visible;
                    return invert ? !result : result;
                }
                return false;
            }
        }

        public class InverseBoolConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                if (value is bool boolValue) return !boolValue;
                return false;
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                if (value is bool boolValue) return !boolValue;
                return false;
            }
        }

        public class CountToVisibilityConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                if (value is int count) return count > 0 ? Visibility.Visible : Visibility.Collapsed;
                return Visibility.Collapsed;
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                throw new NotImplementedException();
            }
        }

        public class RelayCommand : ICommand
        {
            private readonly Action<object?> _execute;
            private readonly Func<object?, bool>? _canExecute;

            public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public RelayCommand(Action execute, Func<bool>? canExecute = null)
                : this(_ => execute(), canExecute != null ? _ => canExecute() : null) { }

            public event EventHandler? CanExecuteChanged
            {
                add => CommandManager.RequerySuggested += value;
                remove => CommandManager.RequerySuggested -= value;
            }

            public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
            public void Execute(object? parameter) => _execute(parameter);
            public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
        }

        public class RelayCommandAsync : ICommand
        {
            private readonly Func<object?, Task> _execute;
            private readonly Func<object?, bool>? _canExecute;
            private bool _isExecuting;

            public RelayCommandAsync(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public RelayCommandAsync(Func<Task> execute, Func<bool>? canExecute = null)
                : this(_ => execute(), canExecute != null ? _ => canExecute() : null) { }

            public event EventHandler? CanExecuteChanged
            {
                add => CommandManager.RequerySuggested += value;
                remove => CommandManager.RequerySuggested -= value;
            }

            public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);

            public async void Execute(object? parameter)
            {
                if (_isExecuting) return;
                _isExecuting = true;
                RaiseCanExecuteChanged();
                try { await _execute(parameter); }
                finally { _isExecuting = false; RaiseCanExecuteChanged(); }
            }

            public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
        }
    }

    // ==========================================================================
    // MODELS
    // ==========================================================================
    namespace Commands.Models
    {
        public enum SortType { CurrentSheetNumber, SheetName, ManualOrder }

        public partial class SheetInfo : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
        {
            private bool _isSelected = true;
            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected != value)
                    {
                        _isSelected = value;
                        OnPropertyChanged(nameof(IsSelected));
                    }
                }
            }

            private string _currentSheetNumber = string.Empty;
            public string CurrentSheetNumber
            {
                get => _currentSheetNumber;
                set { if (_currentSheetNumber != value) { _currentSheetNumber = value; OnPropertyChanged(nameof(CurrentSheetNumber)); } }
            }

            private string _sheetName = string.Empty;
            public string SheetName
            {
                get => _sheetName;
                set { if (_sheetName != value) { _sheetName = value; OnPropertyChanged(nameof(SheetName)); } }
            }

            private string _newSheetNumber = string.Empty;
            public string NewSheetNumber
            {
                get => _newSheetNumber;
                set
                {
                    if (_newSheetNumber != value)
                    {
                        _newSheetNumber = value;
                        OnPropertyChanged(nameof(NewSheetNumber));
                        OnPropertyChanged(nameof(HasChanges));
                        OnPropertyChanged(nameof(DisplaySheetNumber));
                    }
                }
            }

            public int SortOrder { get; set; }
            public bool IsPlaceholder { get; set; }
            public bool IsTemplate { get; set; }
            public ElementId ElementId { get; set; }

            public SheetInfo() { ElementId = ElementId.InvalidElementId; }
            public string DisplaySheetNumber => string.IsNullOrEmpty(NewSheetNumber) ? CurrentSheetNumber : NewSheetNumber;
            public bool HasChanges => !string.IsNullOrEmpty(NewSheetNumber) && NewSheetNumber != CurrentSheetNumber;
        }

        public class NumberingOptions
        {
            public string Prefix { get; set; } = string.Empty;
            public bool UsePrefix { get; set; } = true;
            public int StartNumber { get; set; } = 1;
            public int NumberPadding { get; set; } = 2;
            public bool KeepUnselectedSheetsUnchanged { get; set; } = true;
            public bool UseManualPadding { get; set; } = true;
        }

        public class NumberingResult
        {
            public bool Success { get; set; }
            public int SheetsUpdated { get; set; }
            public int SheetsSkipped { get; set; }
            public List<string> Errors { get; set; } = new();
            public List<string> Warnings { get; set; } = new();

            public string GetSummary()
            {
                var summary = $"Đã cập nhật {SheetsUpdated} sheet";
                if (SheetsSkipped > 0) summary += $", {SheetsSkipped} sheet bị bỏ qua";
                if (Errors.Count > 0) summary += $"\nLỗi: {string.Join("; ", Errors)}";
                return summary;
            }
        }
    }

    // ==========================================================================
    // SERVICES
    // ==========================================================================
    namespace Commands.Services
    {
        using Models;

        public class SheetNumberingService
        {
            private readonly UIDocument? _uidoc;
            private readonly Document? _doc;

            public SheetNumberingService() { _uidoc = null; _doc = null; }
            public SheetNumberingService(UIDocument uidoc) { _uidoc = uidoc; _doc = uidoc.Document; }

            public ObservableCollection<SheetInfo> GetAllSheets()
            {
                var sheets = new ObservableCollection<SheetInfo>();
                LoggingService.LogInfo("Bắt đầu lấy danh sách sheet...");
                var collector = new FilteredElementCollector(_doc!).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().OrderBy(s => s.SheetNumber);
                int index = 0;
                foreach (var viewSheet in collector)
                {
                    sheets.Add(new SheetInfo
                    {
                        ElementId = viewSheet.Id,
                        CurrentSheetNumber = viewSheet.SheetNumber,
                        SheetName = viewSheet.Name,
                        IsSelected = true,
                        SortOrder = index,
                        IsPlaceholder = viewSheet.IsPlaceholder,
                        IsTemplate = IsSheetTemplate(viewSheet)
                    });
                    index++;
                }
                LoggingService.LogInfo($"Đã lấy {sheets.Count} sheet.");
                return sheets;
            }

            private bool IsSheetTemplate(ViewSheet sheet)
            {
                try
                {
                    var planStruct = _doc!.GetElement(sheet.Id) as ViewSheet;
                    if (planStruct == null) return false;
                    var parameters = planStruct.GetParameters("Type");
                    foreach (var param in parameters)
                        if (param.AsValueString()?.Contains("template", StringComparison.OrdinalIgnoreCase) == true) return true;
                    return false;
                }
                catch { return false; }
            }

            public List<string> GeneratePreview(IEnumerable<SheetInfo> selectedSheets, NumberingOptions options, IEnumerable<SheetInfo>? allSheets = null)
            {
                var previewNumbers = new List<string>();
                if (allSheets != null)
                    foreach (var s in allSheets) s.NewSheetNumber = string.Empty;

                var sheetsToNumber = new List<SheetInfo>(selectedSheets.Where(s => s.IsSelected));
                if (sheetsToNumber.Count == 0) return previewNumbers;

                int effectivePadding = ComputeEffectivePadding(sheetsToNumber, options);
                int counter = options.StartNumber;
                foreach (var sheet in sheetsToNumber)
                {
                    var newNumber = GenerateSheetNumber(counter, options, effectivePadding);
                    sheet.NewSheetNumber = newNumber;
                    previewNumbers.Add(newNumber);
                    counter++;
                }
                return previewNumbers;
            }

            public int ComputeEffectivePadding(IList<SheetInfo> sheetsToNumber, NumberingOptions options)
            {
                if (!options.UseManualPadding)
                {
                    int totalCount = sheetsToNumber.Count;
                    int largestNumber = options.StartNumber + Math.Max(0, totalCount - 1);
                    int digitsFromCount = totalCount.ToString().Length;
                    int digitsFromMax = Math.Max(1, largestNumber).ToString().Length;
                    return Math.Max(digitsFromCount, digitsFromMax);
                }
                return Math.Max(0, options.NumberPadding);
            }

            public string GenerateSheetNumber(int number, NumberingOptions options) => GenerateSheetNumber(number, options, options.NumberPadding);

            public string GenerateSheetNumber(int number, NumberingOptions options, int effectivePadding)
            {
                var paddedNumber = number.ToString().PadLeft(effectivePadding, '0');
                return options.UsePrefix ? $"{options.Prefix}{paddedNumber}" : paddedNumber;
            }

            public List<string> CheckForDuplicates(IEnumerable<SheetInfo> allSheets, NumberingOptions options, bool keepUnselectedUnchanged)
            {
                var duplicates = new List<string>();
                var selectedSheets = allSheets.Where(s => s.IsSelected).ToList();
                var newNumbers = selectedSheets.Where(s => !string.IsNullOrEmpty(s.NewSheetNumber)).Select(s => s.NewSheetNumber).ToList();
                var selectedNewNumbers = new HashSet<string>(newNumbers);

                var duplicatesWithin = newNumbers.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                duplicates.AddRange(duplicatesWithin.Select(d => $"Trùng trong danh sách chọn: {d}"));

                if (keepUnselectedUnchanged)
                {
                    var unselectedSheets = allSheets.Where(s => !s.IsSelected).ToList();
                    foreach (var newNum in selectedNewNumbers)
                    {
                        var conflict = unselectedSheets.FirstOrDefault(s => s.CurrentSheetNumber.Equals(newNum, StringComparison.OrdinalIgnoreCase));
                        if (conflict != null) duplicates.Add($"Trùng với sheet không chọn '{conflict.SheetName}': {newNum}");
                    }
                }
                return duplicates.Distinct().ToList();
            }

            public async Task<NumberingResult> ApplyNumberingAsync(IEnumerable<SheetInfo> sheetsToUpdate, NumberingOptions options)
            {
                var result = new NumberingResult();
                if (_doc == null) { result.Success = true; return result; }

                LoggingService.LogInfo("Bắt đầu áp dụng đánh số sheet...");
                var selectedSheets = sheetsToUpdate.Where(s => s.IsSelected && !string.IsNullOrEmpty(s.NewSheetNumber)).ToList();
                var skippedSheets = sheetsToUpdate.Where(s => !s.IsSelected).ToList();
                var selectedSheetsWithChanges = selectedSheets.Where(s => s.HasChanges).ToList();
                result.SheetsSkipped = skippedSheets.Count;

                if (selectedSheetsWithChanges.Count == 0)
                {
                    LoggingService.LogWarning("Không có sheet nào cần thay đổi.");
                    result.Success = true;
                    return result;
                }

                var transactionGroup = new TransactionGroup(_doc, "Renumber Sheets Group");
                try
                {
                    transactionGroup.Start();

                    using (var transaction = new Transaction(_doc, "Phase 1 - Assign Temporary Numbers"))
                    {
                        transaction.Start();
                        int tempIndex = 0;
                        foreach (var sheet in selectedSheetsWithChanges)
                        {
                            var tempNumber = $"__TEMP_{Guid.NewGuid():N}_{tempIndex}";
                            var viewSheet = _doc.GetElement(sheet.ElementId) as ViewSheet;
                            if (viewSheet != null)
                            {
                                viewSheet.SheetNumber = tempNumber;
                                LoggingService.LogDebug($"Gán số tạm: {sheet.CurrentSheetNumber} -> {tempNumber}");
                            }
                            tempIndex++;
                        }
                        transaction.Commit();
                    }

                    using (var transaction = new Transaction(_doc, "Phase 2 - Assign Final Numbers"))
                    {
                        transaction.Start();
                        foreach (var sheet in selectedSheetsWithChanges)
                        {
                            var viewSheet = _doc.GetElement(sheet.ElementId) as ViewSheet;
                            if (viewSheet != null)
                            {
                                try
                                {
                                    viewSheet.SheetNumber = sheet.NewSheetNumber;
                                    sheet.CurrentSheetNumber = sheet.NewSheetNumber;
                                    sheet.NewSheetNumber = string.Empty;
                                    result.SheetsUpdated++;
                                    LoggingService.LogDebug($"Gán số cuối: {sheet.NewSheetNumber} -> {sheet.CurrentSheetNumber}");
                                }
                                catch (Exception ex)
                                {
                                    var errorMsg = $"Lỗi khi gán số cho sheet '{sheet.SheetName}': {ex.Message}";
                                    result.Errors.Add(errorMsg);
                                    LoggingService.LogError(errorMsg);
                                }
                            }
                        }
                        transaction.Commit();
                    }

                    transactionGroup.Assimilate();
                    result.Success = result.Errors.Count == 0;
                    LoggingService.LogInfo($"Hoàn thành. Đã cập nhật {result.SheetsUpdated} sheet, {result.SheetsSkipped} sheet bị bỏ qua.");
                }
                catch (Exception ex)
                {
                    LoggingService.LogError($"Lỗi transaction: {ex.Message}");
                    if (transactionGroup.HasStarted()) { transactionGroup.RollBack(); LoggingService.LogInfo("Đã rollback toàn bộ thay đổi."); }
                    result.Success = false;
                    result.Errors.Add($"Lỗi nghiêm trọng: {ex.Message}");
                }
                return result;
            }

            public List<SheetInfo> GetUnselectedSheets(IEnumerable<SheetInfo> allSheets) => allSheets.Where(s => !s.IsSelected).ToList();
        }
    }

    // ==========================================================================
    // VIEWMODEL
    // ==========================================================================
    namespace Commands.ViewModels
    {
        using Helpers;
        using Models;
        using Services;

        public partial class MainViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
        {
        private readonly UIDocument? _uidoc;
        private readonly SheetNumberingService? _sheetService;
        private readonly bool _isDesignMode;

        // FIX MODELESS TRANSACTION: Store external event and handler for transaction
        private readonly ExternalEvent? _externalEvent;
        private readonly SheetNumberUpdateHandler? _updateHandler;

            [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private ObservableCollection<SheetInfo> _sheets = new();
            [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private SheetInfo? _selectedSheet;

            private string _prefix = "A-";
            public string Prefix
            {
                get => _prefix;
                set
                {
                    if (_prefix != value)
                    {
                        _prefix = value;
                        OnPropertyChanged(nameof(Prefix));
                        OnNumberingSettingChanged();
                    }
                }
            }

            private bool _usePrefix = true;
            public bool UsePrefix
            {
                get => _usePrefix;
                set
                {
                    if (_usePrefix != value)
                    {
                        _usePrefix = value;
                        OnPropertyChanged(nameof(UsePrefix));
                        OnNumberingSettingChanged();
                    }
                }
            }

            private int _startNumber = 1;
            public int StartNumber
            {
                get => _startNumber;
                set
                {
                    if (_startNumber != value)
                    {
                        _startNumber = value;
                        OnPropertyChanged(nameof(StartNumber));
                        OnNumberingSettingChanged();
                    }
                }
            }

            private int _numberPadding = 2;
            public int NumberPadding
            {
                get => _numberPadding;
                set
                {
                    if (_numberPadding != value)
                    {
                        _numberPadding = value;
                        OnPropertyChanged(nameof(NumberPadding));
                        OnNumberingSettingChanged();
                    }
                }
            }

            private bool _useManualPadding = false;
            public bool UseManualPadding
            {
                get => _useManualPadding;
                set
                {
                    if (_useManualPadding != value)
                    {
                        _useManualPadding = value;
                        OnPropertyChanged(nameof(UseManualPadding));
                        OnNumberingSettingChanged();
                    }
                }
            }
            [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _keepUnselectedSheets = true;
            [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private Models.SortType _currentSortType = Models.SortType.CurrentSheetNumber;
            [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string _statusMessage = "Sẵn sàng";
            [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _isBusy;
            [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string _errorMessage = string.Empty;
            [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _hasError;
            [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private int _selectedCount;
            [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private int _totalCount;
            [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private int _autoDetectedPadding = 1;

            public ICommand PreviewCommand { get; set; }
            public ICommand ApplyCommand { get; set; }
            // FIX MODELESS TRANSACTION: New command for OK button - applies and closes window
            public ICommand ApplyAndCloseCommand { get; set; }
            public ICommand CancelCommand { get; set; }
            public ICommand MoveUpCommand { get; set; }
            public ICommand MoveDownCommand { get; set; }
            public ICommand SortByNumberCommand { get; set; }
            public ICommand SortByNameCommand { get; set; }
            public ICommand SortByManualCommand { get; set; }
            public ICommand SelectAllCommand { get; set; }
            public ICommand DeselectAllCommand { get; set; }

            public event EventHandler<NumberingResult>? Completed;
            public event EventHandler? Cancelled;
            // FIX MODELESS TRANSACTION: Event to request window close from ViewModel
            public event EventHandler? RequestClose;

            public MainViewModel()
            {
                _isDesignMode = true;
                InitializeCommands();
                LoadDesignTimeData();
            }

            // FIX MODELESS TRANSACTION: Constructor for Revit context with ExternalEvent
            public MainViewModel(UIDocument uidoc, ExternalEvent externalEvent, SheetNumberUpdateHandler updateHandler) : this()
            {
                _isDesignMode = false;
                _uidoc = uidoc;
                _externalEvent = externalEvent;
                _updateHandler = updateHandler;
                _sheetService = new SheetNumberingService(uidoc);
                LoadSheetsFromRevit();
            }

            public MainViewModel(UIDocument uidoc) : this()
            {
                _isDesignMode = false;
                _uidoc = uidoc;
                _sheetService = new SheetNumberingService(uidoc);
                LoadSheetsFromRevit();
            }

            private void InitializeCommands()
            {
                PreviewCommand = new RelayCommand(ExecutePreview, CanExecutePreview);
                ApplyCommand = new RelayCommandAsync(ExecuteApplyAsync, CanExecuteApply);
                // FIX MODELESS TRANSACTION: Apply and close command for OK button
                ApplyAndCloseCommand = new RelayCommandAsync(ExecuteApplyAndCloseAsync, CanExecuteApply);
                CancelCommand = new RelayCommand(ExecuteCancel);
                MoveUpCommand = new RelayCommand(ExecuteMoveUp, CanExecuteMoveUp);
                MoveDownCommand = new RelayCommand(ExecuteMoveDown, CanExecuteMoveDown);
                SortByNumberCommand = new RelayCommand(_ => SortSheets(Models.SortType.CurrentSheetNumber));
                SortByNameCommand = new RelayCommand(_ => SortSheets(Models.SortType.SheetName));
                SortByManualCommand = new RelayCommand(_ => SortSheets(Models.SortType.ManualOrder));
                SelectAllCommand = new RelayCommand(_ => SelectAll());
                DeselectAllCommand = new RelayCommand(_ => DeselectAll());
            }

            private void LoadDesignTimeData()
            {
                Sheets = new ObservableCollection<SheetInfo>
                {
                    new SheetInfo { IsSelected = true, CurrentSheetNumber = "A-01", SheetName = "Mặt bằng tầng 1", NewSheetNumber = "A-01", SortOrder = 0 },
                    new SheetInfo { IsSelected = true, CurrentSheetNumber = "A-02", SheetName = "Mặt bằng tầng 2", NewSheetNumber = "A-02", SortOrder = 1 },
                    new SheetInfo { IsSelected = true, CurrentSheetNumber = "A-03", SheetName = "Mặt đứng", NewSheetNumber = "A-03", SortOrder = 2 },
                    new SheetInfo { IsSelected = false, CurrentSheetNumber = "B-01", SheetName = "Mặt cắt A-A", NewSheetNumber = "", SortOrder = 3 },
                    new SheetInfo { IsSelected = false, CurrentSheetNumber = "B-02", SheetName = "Chi tiết cột", NewSheetNumber = "", SortOrder = 4 }
                };
                SubscribeSheetsSelection();
                TotalCount = Sheets.Count;
                UpdateSelectedCount();
                StatusMessage = "Chế độ xem trước (Design Mode)";
            }

            private void LoadSheets()
            {
                try
                {
                    IsBusy = true;
                    StatusMessage = "Đang tải danh sách sheet...";
                    Sheets = _sheetService!.GetAllSheets();
                    SubscribeSheetsSelection();
                    TotalCount = Sheets.Count;
                    UpdateSelectedCount();
                    SortSheets(Models.SortType.CurrentSheetNumber);
                    StatusMessage = $"Đã tải {TotalCount} sheet";
                    LoggingService.LogInfo($"Đã tải {TotalCount} sheet từ Revit");
                }
                catch (Exception ex)
                {
                    StatusMessage = "Lỗi khi tải sheet";
                    ErrorMessage = ex.Message;
                    HasError = true;
                    LoggingService.LogError($"Lỗi khi tải sheet: {ex.Message}");
                }
                finally { IsBusy = false; }
            }

            private void SubscribeSheetsSelection()
            {
                foreach (var s in Sheets) s.PropertyChanged -= OnSheetPropertyChanged;
                foreach (var s in Sheets) s.PropertyChanged += OnSheetPropertyChanged;
            }

            private void OnSheetPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName != nameof(SheetInfo.IsSelected)) return;
                if (sender is not SheetInfo sheet) return;
                if (!sheet.IsSelected && !string.IsNullOrEmpty(sheet.NewSheetNumber))
                    sheet.NewSheetNumber = string.Empty;
                UpdateSelectedCount();
                OnNumberingSettingChanged();
            }

            /// <summary>
            /// Called whenever a numbering-related setting changes (Prefix, StartNumber, Padding,
            /// UsePrefix, UseManualPadding, or IsSelected). Clears stale error state, refreshes
            /// preview numbers, and re-evaluates command availability so the Apply button
            /// is never permanently stuck after a validation failure.
            /// </summary>
            private void OnNumberingSettingChanged()
            {
                ClearError();
                RefreshPreviewIfNeeded();
                CommandManager.InvalidateRequerySuggested();
            }

            /// <summary>
            /// Regenerates preview numbers from current settings if a preview was previously
            /// generated (i.e., at least one sheet has a NewSheetNumber). This ensures the
            /// preview data stays in sync after any setting change.
            /// </summary>
            private void RefreshPreviewIfNeeded()
            {
                if (_isDesignMode || _sheetService == null) return;
                // Only refresh if a preview was already generated
                if (Sheets.All(s => string.IsNullOrEmpty(s.NewSheetNumber))) return;
                var options = GetNumberingOptions();
                var selectedSheets = Sheets.Where(s => s.IsSelected).ToList();
                if (selectedSheets.Count > 0 && (!UsePrefix || !string.IsNullOrWhiteSpace(Prefix)))
                {
                    _sheetService.GeneratePreview(selectedSheets, options, Sheets);
                    OnPropertyChanged(nameof(Sheets));
                }
            }

            private void LoadSheetsFromRevit()
            {
                if (_isDesignMode) LoadDesignTimeData();
                else LoadSheets();
            }

            private void RecomputeAutoPadding()
            {
                if (UseManualPadding) return;
                var sheetsToNumber = Sheets.Where(s => s.IsSelected).ToList();
                int totalCount = sheetsToNumber.Count;
                int largestNumber = StartNumber + Math.Max(0, totalCount - 1);
                int digitsFromCount = totalCount.ToString().Length;
                int digitsFromMax = Math.Max(1, largestNumber).ToString().Length;
                AutoDetectedPadding = Math.Max(digitsFromCount, digitsFromMax);
            }

            public void UpdateSelectedCount()
            {
                SelectedCount = Sheets.Count(s => s.IsSelected);
                OnPropertyChanged(nameof(Sheets));
                RecomputeAutoPadding();
            }

            private void SortSheets(Models.SortType sortType)
            {
                CurrentSortType = sortType;
                var sorted = sortType switch
                {
                    Models.SortType.CurrentSheetNumber => Sheets.OrderBy(s => s.CurrentSheetNumber, new NaturalSortComparer()).ToList(),
                    Models.SortType.SheetName => Sheets.OrderBy(s => s.SheetName).ToList(),
                    Models.SortType.ManualOrder => Sheets.OrderBy(s => s.SortOrder).ToList(),
                    _ => Sheets.ToList()
                };
                for (int i = 0; i < sorted.Count; i++) sorted[i].SortOrder = i;
                Sheets = new ObservableCollection<SheetInfo>(sorted);
                SubscribeSheetsSelection();
                OnPropertyChanged(nameof(Sheets));
            }

            private void SelectAll() { foreach (var sheet in Sheets) sheet.IsSelected = true; UpdateSelectedCount(); LoggingService.LogInfo("Đã chọn tất cả sheet"); }
            private void DeselectAll() { foreach (var sheet in Sheets) sheet.IsSelected = false; UpdateSelectedCount(); LoggingService.LogInfo("Đã bỏ chọn tất cả sheet"); }

            private bool CanExecutePreview(object? parameter)
            {
                if (!IsBusy && Sheets.Any(s => s.IsSelected))
                    if (!UsePrefix || !string.IsNullOrWhiteSpace(Prefix)) return true;
                return false;
            }

            private void ExecutePreview(object? parameter)
            {
                try
                {
                    if (_isDesignMode) return;
                    ClearError();
                    IsBusy = true;
                    StatusMessage = "Đang tạo xem trước...";
                    var selectedSheets = Sheets.Where(s => s.IsSelected).ToList();

                    if (selectedSheets.Count == 0) { SetError("Vui lòng chọn ít nhất một sheet để đánh số."); return; }
                    if (CurrentSortType != Models.SortType.ManualOrder) SortSheets(Models.SortType.ManualOrder);

                    var options = GetNumberingOptions();
                    var duplicates = _sheetService!.CheckForDuplicates(Sheets, options, KeepUnselectedSheets);
                    if (duplicates.Count > 0) { SetError("Phát hiện trùng số:\n" + string.Join("\n", duplicates)); return; }

                    _sheetService.GeneratePreview(selectedSheets, options, Sheets);
                    OnPropertyChanged(nameof(Sheets));
                    StatusMessage = $"Xem trước: {selectedSheets.Count} sheet sẽ được đánh số";
                    LoggingService.LogInfo($"Tạo xem trước cho {selectedSheets.Count} sheet");
                }
                catch (Exception ex) { SetError($"Lỗi khi tạo xem trước: {ex.Message}"); LoggingService.LogError($"Lỗi preview: {ex.Message}"); }
                finally { IsBusy = false; }
            }

            // Buttons always enabled - CanExecuteApply always returns true
            private bool CanExecuteApply(object? parameter) => true;

            private async Task ExecuteApplyAsync(object? parameter)
            {
                try
                {
                    if (_isDesignMode) return;
                    // FIX MODELESS TRANSACTION: If no ExternalEvent, fall back to direct service call (for Add-in Manager)
                    if (_externalEvent == null || _updateHandler == null)
                    {
                        await ExecuteApplyDirectAsync();
                        return;
                    }

                    ClearError();
                    IsBusy = true;
                    StatusMessage = "Đang áp dụng đánh số...";

                    // Always regenerate preview from current settings to avoid stale data
                    var options = GetNumberingOptions();
                    var selectedSheets = Sheets.Where(s => s.IsSelected).ToList();

                    if (selectedSheets.Count == 0) { SetError("Vui lòng chọn ít nhất một sheet để đánh số."); return; }

                    _sheetService!.GeneratePreview(selectedSheets, options, Sheets);
                    OnPropertyChanged(nameof(Sheets));

                    var sheetsWithChanges = Sheets.Where(s => s.IsSelected && s.HasChanges).ToList();
                    if (sheetsWithChanges.Count == 0) { SetError("Không có sheet nào được thay đổi."); return; }

                    // Always run fresh duplicate validation - never use cached results
                    var duplicates = _sheetService.CheckForDuplicates(Sheets, options, KeepUnselectedSheets);
                    if (duplicates.Count > 0) { SetError("Phát hiện trùng số:\n" + string.Join("\n", duplicates)); return; }

                    // FIX MODELESS TRANSACTION: Set data for handler and raise external event
                    _updateHandler.SetUpdateData(Sheets, options, async (result) =>
                    {
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                        {
                            if (result.Success)
                            {
                                ReloadSheetsAfterApply();
                                StatusMessage = result.GetSummary();
                                MessageBox.Show(result.GetSummary(), "Thành công", MessageBoxButton.OK, MessageBoxImage.Information);
                                Completed?.Invoke(this, result);
                            }
                            else { SetError($"Áp dụng thất bại:\n{string.Join("\n", result.Errors)}"); }
                            IsBusy = false;
                            CommandManager.InvalidateRequerySuggested();
                        });
                    });

                    var raiseResult = _externalEvent.Raise();
                    LoggingService.LogInfo($"ExternalEvent raised with result: {raiseResult}");

                    // FIX MODELESS TRANSACTION: Check if event was accepted or pending
                    // ExternalEventRequest enum values: Accepted, Pending, Denied
                    if (raiseResult != ExternalEventRequest.Accepted && raiseResult != ExternalEventRequest.Pending)
                    {
                        LoggingService.LogWarning($"ExternalEvent không được chấp nhận: {raiseResult}");
                    }
                }
                catch (Exception ex) { SetError($"Lỗi khi áp dụng: {ex.Message}"); LoggingService.LogError($"Lỗi apply: {ex.Message}"); IsBusy = false; CommandManager.InvalidateRequerySuggested(); }
            }

            // FIX MODELESS TRANSACTION: Fallback method for Add-in Manager (when no ExternalEvent)
            private async Task ExecuteApplyDirectAsync()
            {
                try
                {
                    ClearError();
                    IsBusy = true;
                    StatusMessage = "Đang áp dụng đánh số (Direct)...";

                    var options = GetNumberingOptions();
                    var selectedSheets = Sheets.Where(s => s.IsSelected).ToList();

                    if (selectedSheets.Count == 0) { SetError("Vui lòng chọn ít nhất một sheet để đánh số."); return; }

                    _sheetService!.GeneratePreview(selectedSheets, options, Sheets);
                    OnPropertyChanged(nameof(Sheets));

                    var sheetsWithChanges = Sheets.Where(s => s.IsSelected && s.HasChanges).ToList();
                    if (sheetsWithChanges.Count == 0) { SetError("Không có sheet nào được thay đổi."); return; }

                    var duplicates = _sheetService.CheckForDuplicates(Sheets, options, KeepUnselectedSheets);
                    if (duplicates.Count > 0) { SetError("Phát hiện trùng số:\n" + string.Join("\n", duplicates)); return; }

                    var result = await _sheetService.ApplyNumberingAsync(Sheets, options);

                    if (result.Success)
                    {
                        ReloadSheetsAfterApply();
                        StatusMessage = result.GetSummary();
                        MessageBox.Show(result.GetSummary(), "Thành công", MessageBoxButton.OK, MessageBoxImage.Information);
                        Completed?.Invoke(this, result);
                    }
                    else { SetError($"Áp dụng thất bại:\n{string.Join("\n", result.Errors)}"); }
                }
                catch (Exception ex) { SetError($"Lỗi khi áp dụng: {ex.Message}"); LoggingService.LogError($"Lỗi apply direct: {ex.Message}"); }
                finally { IsBusy = false; CommandManager.InvalidateRequerySuggested(); }
            }

            private void ReloadSheetsAfterApply()
            {
                if (_sheetService == null) return;
                var selectionState = Sheets.ToDictionary(s => s.ElementId, s => s.IsSelected);
                ElementId idToSelect = SelectedSheet?.ElementId ?? ElementId.InvalidElementId;

                var freshSheets = _sheetService.GetAllSheets();
                foreach (var s in freshSheets)
                    if (selectionState.TryGetValue(s.ElementId, out var wasSelected)) s.IsSelected = wasSelected;

                Sheets = new ObservableCollection<SheetInfo>(freshSheets);
                SortSheets(Models.SortType.CurrentSheetNumber);

                if (idToSelect != null && idToSelect != ElementId.InvalidElementId)
                    SelectedSheet = Sheets.FirstOrDefault(s => s.ElementId == idToSelect);

                UpdateSelectedCount();

                var options = GetNumberingOptions();
                var selectedSheetsForPreview = Sheets.Where(s => s.IsSelected).ToList();
                if (selectedSheetsForPreview.Count > 0 && (!UsePrefix || !string.IsNullOrWhiteSpace(Prefix)))
                {
                    _sheetService.GeneratePreview(selectedSheetsForPreview, options, Sheets);
                    OnPropertyChanged(nameof(Sheets));
                }
            }

            // FIX MODELESS TRANSACTION: Apply changes and then close window
            private async Task ExecuteApplyAndCloseAsync(object? parameter)
            {
                try
                {
                    if (_isDesignMode) return;

                    // FIX MODELESS TRANSACTION: For OK button with ExternalEvent, add RequestClose to callback
                    if (_externalEvent != null && _updateHandler != null)
                    {
                        ClearError();
                        IsBusy = true;
                        StatusMessage = "Đang áp dụng đánh số...";

                        var options = GetNumberingOptions();
                        var selectedSheets = Sheets.Where(s => s.IsSelected).ToList();

                        if (selectedSheets.Count == 0) { SetError("Vui lòng chọn ít nhất một sheet để đánh số."); return; }

                        _sheetService!.GeneratePreview(selectedSheets, options, Sheets);
                        OnPropertyChanged(nameof(Sheets));

                        var sheetsWithChanges = Sheets.Where(s => s.IsSelected && s.HasChanges).ToList();
                        if (sheetsWithChanges.Count == 0) { SetError("Không có sheet nào được thay đổi."); return; }

                        var duplicates = _sheetService.CheckForDuplicates(Sheets, options, KeepUnselectedSheets);
                        if (duplicates.Count > 0) { SetError("Phát hiện trùng số:\n" + string.Join("\n", duplicates)); return; }

                        // FIX MODELESS TRANSACTION: Modified callback - window stays open after apply
                        _updateHandler.SetUpdateData(Sheets, options, async (result) =>
                        {
                            await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                            {
                                if (result.Success)
                                {
                                    ReloadSheetsAfterApply();
                                    StatusMessage = result.GetSummary();
                                    MessageBox.Show(result.GetSummary(), "Thành công", MessageBoxButton.OK, MessageBoxImage.Information);
                                    Completed?.Invoke(this, result);
                                    // FIX MODELESS TRANSACTION: Window stays open - user can apply again
                                }
                                else
                                {
                                    SetError($"Áp dụng thất bại:\n{string.Join("\n", result.Errors)}");
                                }
                                IsBusy = false;
                                CommandManager.InvalidateRequerySuggested();
                            });
                        });

                        var raiseResult = _externalEvent.Raise();
                        LoggingService.LogInfo($"ExternalEvent raised (OK button) with result: {raiseResult}");

                        if (raiseResult != ExternalEventRequest.Accepted && raiseResult != ExternalEventRequest.Pending)
                        {
                            LoggingService.LogWarning($"ExternalEvent không được chấp nhận: {raiseResult}");
                        }
                    }
                    else
                    {
                        // FIX MODELESS TRANSACTION: Fallback for Add-in Manager - window stays open
                        await ExecuteApplyDirectAsync();
                    }
                }
                catch (Exception ex) { SetError($"Lỗi khi áp dụng: {ex.Message}"); LoggingService.LogError($"Lỗi apply OK: {ex.Message}"); IsBusy = false; CommandManager.InvalidateRequerySuggested(); }
            }

            private void ExecuteCancel(object? parameter) => Cancelled?.Invoke(this, EventArgs.Empty);

            private bool CanExecuteMoveUp(object? parameter)
            {
                var sheet = parameter as SheetInfo ?? SelectedSheet;
                if (sheet == null) return false;
                return Sheets.IndexOf(sheet) > 0;
            }

            private void ExecuteMoveUp(object? parameter)
            {
                var sheet = parameter as SheetInfo ?? SelectedSheet;
                if (sheet == null) return;
                var index = Sheets.IndexOf(sheet);
                if (index <= 0) return;
                Sheets.Move(index, index - 1);
                UpdateSortOrders();
                SelectedSheet = sheet;
                RefreshPreviewAfterReorder();
                CommandManager.InvalidateRequerySuggested();
            }

            private bool CanExecuteMoveDown(object? parameter)
            {
                var sheet = parameter as SheetInfo ?? SelectedSheet;
                if (sheet == null) return false;
                var index = Sheets.IndexOf(sheet);
                return index >= 0 && index < Sheets.Count - 1;
            }

            private void ExecuteMoveDown(object? parameter)
            {
                var sheet = parameter as SheetInfo ?? SelectedSheet;
                if (sheet == null) return;
                var index = Sheets.IndexOf(sheet);
                if (index < 0 || index >= Sheets.Count - 1) return;
                Sheets.Move(index, index + 1);
                UpdateSortOrders();
                SelectedSheet = sheet;
                RefreshPreviewAfterReorder();
                CommandManager.InvalidateRequerySuggested();
            }

            private void UpdateSortOrders() { for (int i = 0; i < Sheets.Count; i++) Sheets[i].SortOrder = i; }

            public void RefreshPreview()
            {
                if (_isDesignMode) return;
                if (Sheets.All(s => string.IsNullOrEmpty(s.NewSheetNumber))) return;
                var options = GetNumberingOptions();
                var selectedSheets = Sheets.Where(s => s.IsSelected).ToList();
                _sheetService!.GeneratePreview(selectedSheets, options, Sheets);
                OnPropertyChanged(nameof(Sheets));
                LoggingService.LogInfo($"Refresh preview: {selectedSheets.Count} sheet(s) được chọn");
            }

            private void RefreshPreviewAfterReorder()
            {
                if (_isDesignMode) return;
                if (Sheets.All(s => string.IsNullOrEmpty(s.NewSheetNumber))) return;
                var options = GetNumberingOptions();
                var selectedSheets = Sheets.Where(s => s.IsSelected).ToList();
                _sheetService!.GeneratePreview(selectedSheets, options, Sheets);
                OnPropertyChanged(nameof(Sheets));
            }

            private NumberingOptions GetNumberingOptions()
            {
                return new NumberingOptions
                {
                    Prefix = UsePrefix ? (Prefix ?? string.Empty).Trim() : string.Empty,
                    UsePrefix = UsePrefix,
                    StartNumber = StartNumber,
                    NumberPadding = NumberPadding,
                    KeepUnselectedSheetsUnchanged = KeepUnselectedSheets,
                    UseManualPadding = UseManualPadding
                };
            }

            private void SetError(string message) { ErrorMessage = message; HasError = true; }
            private void ClearError() { ErrorMessage = string.Empty; HasError = false; }
        }
    }

    // ==========================================================================
    // RIBBON APPLICATION
    // ==========================================================================
    [Transaction(TransactionMode.Manual)]
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            string tabName = "TOOLS API";
            try { application.CreateRibbonTab(tabName); }
            catch { /* Tab đã tồn tại */ }

            RibbonPanel panel = null;
            foreach (var p in application.GetRibbonPanels(tabName))
                if (p.Name == "Sheet Numbering") { panel = p; break; }

            if (panel == null)
                panel = application.CreateRibbonPanel(tabName, "Sheet Numbering");

            // --- Load logo images using pack:// URI (for Resource build action) ---
            System.Windows.Media.ImageSource? largeImage = null;
            System.Windows.Media.ImageSource? smallImage = null;

            var assemblyName = typeof(RenumberSheetsCommand).Assembly.GetName().Name;

            // Load 32x32 logo for LargeImage
            largeImage = LoadImageFromPackUri(assemblyName, "Resources/icon32.png");
            // Load 16x16 logo for Image
            smallImage = LoadImageFromPackUri(assemblyName, "Resources/icon16.png");
            if (smallImage == null && largeImage != null)
                smallImage = ResizeImage(largeImage, 16, 16);

            // Button text displayed under the icon on Ribbon
            string buttonText = "Sheet\nNumbering";

            var buttonData = new PushButtonData(
                "RenumberSheets",
                buttonText,
                typeof(RenumberSheetsCommand).Assembly.Location,
                typeof(RenumberSheetsCommand).FullName
            );

            buttonData.ToolTip = "Đánh Số Sheet";
            buttonData.LongDescription = "Mở công cụ đánh số Sheet Number tự động.";

            if (largeImage != null) buttonData.LargeImage = largeImage;
            if (smallImage != null) buttonData.Image = smallImage;

            // Add to panel
            panel.AddItem(buttonData);
            return Result.Succeeded;
        }

        private static System.Windows.Media.ImageSource? LoadImageFromPackUri(string assemblyName, string resourcePath)
        {
            try
            {
                var uri = new Uri($"pack://application:,,,/{assemblyName};component/{resourcePath}");
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = uri;
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                LoggingService.LogInfo($"Logo loaded: {resourcePath}");
                return bitmap;
            }
            catch (Exception ex)
            {
                LoggingService.LogError($"Failed to load logo '{resourcePath}': {ex.Message}");
                return null;
            }
        }

        private static System.Windows.Media.ImageSource? ResizeImage(System.Windows.Media.ImageSource source, int width, int height)
        {
            try
            {
                var visual = new System.Windows.Media.DrawingVisual();
                using (var ctx = visual.RenderOpen())
                {
                    ctx.DrawImage(source, new System.Windows.Rect(0, 0, width, height));
                }
                var renderBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                renderBitmap.Render(visual);
                renderBitmap.Freeze();
                return renderBitmap;
            }
            catch (Exception ex)
            {
                LoggingService.LogError($"Failed to resize image: {ex.Message}");
                return null;
            }
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
    }

    // ==========================================================================
    // MAIN COMMAND
    // ==========================================================================
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class RenumberSheetsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                LoggingService.LogInfo("=== Bắt đầu lệnh Renumber Sheets ===");

                var uiDoc = commandData.Application.ActiveUIDocument;
                if (uiDoc == null) { message = "Không thể truy cập tài liệu Revit."; LoggingService.LogError(message); return Result.Failed; }

                var doc = uiDoc.Document;
                if (doc == null) { message = "Không thể truy cập Revit Document."; LoggingService.LogError(message); return Result.Failed; }

                LoggingService.LogInfo($"Mở file: {doc.Title}");

                // FIX MODELESS TRANSACTION: Create ExternalEvent handler for Modeless WPF Window transaction
                var updateHandler = new SheetNumberUpdateHandler();
                var externalEvent = ExternalEvent.Create(updateHandler);

                // FIX MODELESS TRANSACTION: Pass ExternalEvent and Handler to ViewModel
                var viewModel = new Commands.ViewModels.MainViewModel(uiDoc, externalEvent, updateHandler);
                var mainWindow = new Views.MainWindow(viewModel);

                // FIX MODELESS TRANSACTION: Use Show() instead of ShowDialog() - window becomes modeless
                // This allows Revit to remain responsive and ExternalEvent to execute properly
                mainWindow.Show();

                // FIX MODELESS TRANSACTION: Handle Closed event to log when window is closed
                mainWindow.Closed += (sender, args) =>
                {
                    LoggingService.LogInfo("Cửa sổ đã đóng");
                };

                viewModel.Completed += (s, e) => LoggingService.LogInfo($"Hoàn thành: {e.SheetsUpdated} sheet được cập nhật");
                viewModel.Cancelled += (s, e) => LoggingService.LogInfo("Người dùng hủy thao tác");

                // FIX MODELESS TRANSACTION: Return Succeeded immediately since window is now modeless
                // The command doesn't block waiting for window to close
                LoggingService.LogInfo("Lệnh hoàn thành - Window đang mở (Modeless)");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = $"Lỗi không mong đợi: {ex.Message}";
                LoggingService.LogError($"Exception: {ex}");
                LoggingService.LogError($"Stack Trace: {ex.StackTrace}");
                return Result.Failed;
            }
        }
    }
}
