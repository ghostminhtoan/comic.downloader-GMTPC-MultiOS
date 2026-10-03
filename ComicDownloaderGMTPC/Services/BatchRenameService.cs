using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ComicDownloaderGMTPC.Services;

public class RenameItem : ObservableObject
{
    private string _originalPath = string.Empty;
    public string OriginalPath
    {
        get => _originalPath;
        set
        {
            if (SetProperty(ref _originalPath, value))
            {
                OnPropertyChanged(nameof(OriginalName));
                OnPropertyChanged(nameof(DirectoryPath));
            }
        }
    }

    public string OriginalName => Path.GetFileName(OriginalPath);

    private string _newName = string.Empty;
    public string NewName
    {
        get => _newName;
        set
        {
            if (SetProperty(ref _newName, value))
            {
                OnPropertyChanged(nameof(IsChanged));
                OnPropertyChanged(nameof(StatusColor));
            }
        }
    }

    private bool _isDirectory;
    public bool IsDirectory
    {
        get => _isDirectory;
        set => SetProperty(ref _isDirectory, value);
    }

    private bool _isValid = true;
    public bool IsValid
    {
        get => _isValid;
        set
        {
            if (SetProperty(ref _isValid, value))
            {
                OnPropertyChanged(nameof(StatusColor));
            }
        }
    }

    private string _statusText = "✓ Hợp lệ";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string StatusColor => !IsValid ? "#EF4444" : (IsChanged ? "#10B981" : "#94A3B8");
    public bool IsChanged => !string.Equals(OriginalName, NewName, StringComparison.Ordinal);
    public string DirectoryPath => Path.GetDirectoryName(OriginalPath) ?? string.Empty;
}

public abstract partial class RenameMethodBase : ObservableObject
{
    public string Id { get; } = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _icon = "🏷️";

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private string _applyTo = "Name"; // "Name", "Extension", "Both"

    [ObservableProperty]
    private bool _backwards = false;

    [ObservableProperty]
    private bool _useRegex = false;

    public event Action? ConfigurationChanged;
    public void NotifyConfigChanged() => ConfigurationChanged?.Invoke();

    partial void OnIsEnabledChanged(bool value) => NotifyConfigChanged();
    partial void OnApplyToChanged(string value) => NotifyConfigChanged();
    partial void OnBackwardsChanged(bool value) => NotifyConfigChanged();
    partial void OnUseRegexChanged(bool value) => NotifyConfigChanged();

    public abstract string Apply(string currentName, int index, int totalCount, string originalPath, bool isDirectory);
}

// 1. New Name Method
public partial class NewNameMethod : RenameMethodBase
{
    [ObservableProperty]
    private string _format = "<Name>";

    public NewNameMethod()
    {
        Name = "Tên mới (New Name)";
        Icon = "🏷️";
    }

    partial void OnFormatChanged(string value) => NotifyConfigChanged();

    public override string Apply(string currentName, int index, int totalCount, string originalPath, bool isDirectory)
    {
        if (string.IsNullOrEmpty(Format)) return currentName;

        string namePart = isDirectory ? currentName : Path.GetFileNameWithoutExtension(currentName);
        string ext = isDirectory ? string.Empty : Path.GetExtension(currentName);
        if (ext.StartsWith(".")) ext = ext.Substring(1);

        string result = Format;
        result = result.Replace("<Name>", namePart);
        result = result.Replace("<Ext>", ext);

        // <Counter:N>
        result = Regex.Replace(result, @"<Counter:(\d+)>", m =>
        {
            if (int.TryParse(m.Groups[1].Value, out int pad))
            {
                return (index + 1).ToString().PadLeft(pad, '0');
            }
            return (index + 1).ToString();
        });
        result = result.Replace("<Counter>", (index + 1).ToString());

        // <Date:format>
        result = Regex.Replace(result, @"<Date:([^>]+)>", m =>
        {
            try { return DateTime.Now.ToString(m.Groups[1].Value); }
            catch { return DateTime.Now.ToString("yyyy-MM-dd"); }
        });
        result = result.Replace("<Date>", DateTime.Now.ToString("yyyy-MM-dd"));

        if (isDirectory || ApplyTo == "Both")
        {
            return result;
        }

        if (ApplyTo == "Name")
        {
            return string.IsNullOrEmpty(ext) ? result : $"{result}.{ext}";
        }
        else if (ApplyTo == "Extension")
        {
            return string.IsNullOrEmpty(result) ? namePart : $"{namePart}.{result}";
        }

        return result;
    }
}

// 2. Replace Method
public partial class ReplaceMethod : RenameMethodBase
{
    [ObservableProperty]
    private string _findText = string.Empty;

    [ObservableProperty]
    private string _replaceWithText = string.Empty;

    [ObservableProperty]
    private bool _caseSensitive = false;

    public ReplaceMethod()
    {
        Name = "Thay thế (Replace)";
        Icon = "🔄";
    }

    partial void OnFindTextChanged(string value) => NotifyConfigChanged();
    partial void OnReplaceWithTextChanged(string value) => NotifyConfigChanged();
    partial void OnCaseSensitiveChanged(bool value) => NotifyConfigChanged();

    public override string Apply(string currentName, int index, int totalCount, string originalPath, bool isDirectory)
    {
        if (string.IsNullOrEmpty(FindText)) return currentName;

        string namePart = isDirectory ? currentName : Path.GetFileNameWithoutExtension(currentName);
        string ext = isDirectory ? string.Empty : Path.GetExtension(currentName);
        if (ext.StartsWith(".")) ext = ext.Substring(1);

        string target = isDirectory || ApplyTo == "Both" ? currentName : (ApplyTo == "Extension" ? ext : namePart);

        string result = target;
        try
        {
            if (UseRegex)
            {
                var options = CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                if (Backwards) options |= RegexOptions.RightToLeft;
                result = Regex.Replace(target, FindText, ReplaceWithText ?? string.Empty, options);
            }
            else
            {
                var comparison = CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                if (!Backwards)
                {
                    result = ReplaceString(target, FindText, ReplaceWithText ?? string.Empty, comparison);
                }
                else
                {
                    int lastIdx = target.LastIndexOf(FindText, comparison);
                    if (lastIdx >= 0)
                    {
                        result = target.Remove(lastIdx, FindText.Length).Insert(lastIdx, ReplaceWithText ?? string.Empty);
                    }
                }
            }
        }
        catch { }

        if (isDirectory || ApplyTo == "Both") return result;

        if (ApplyTo == "Name")
        {
            return string.IsNullOrEmpty(ext) ? result : $"{result}.{ext}";
        }
        else
        {
            return string.IsNullOrEmpty(result) ? namePart : $"{namePart}.{result}";
        }
    }

    private static string ReplaceString(string str, string oldValue, string newValue, StringComparison comp)
    {
        var sb = new StringBuilder();
        int prev = 0;
        int idx = str.IndexOf(oldValue, comp);
        while (idx != -1)
        {
            sb.Append(str, prev, idx - prev);
            sb.Append(newValue);
            prev = idx + oldValue.Length;
            idx = str.IndexOf(oldValue, prev, comp);
        }
        sb.Append(str, prev, str.Length - prev);
        return sb.ToString();
    }
}

// 3. Renumber Method
public partial class RenumberMethod : RenameMethodBase
{
    [ObservableProperty]
    private int _startNumber = 1;

    [ObservableProperty]
    private int _step = 1;

    [ObservableProperty]
    private int _zeroPadding = 3;

    [ObservableProperty]
    private string _location = "Prepend"; // "Prepend", "Append", "Replace"

    [ObservableProperty]
    private string _separator = " - ";

    public RenumberMethod()
    {
        Name = "Đánh số lại (Renumber)";
        Icon = "🔢";
    }

    partial void OnStartNumberChanged(int value) => NotifyConfigChanged();
    partial void OnStepChanged(int value) => NotifyConfigChanged();
    partial void OnZeroPaddingChanged(int value) => NotifyConfigChanged();
    partial void OnLocationChanged(string value) => NotifyConfigChanged();
    partial void OnSeparatorChanged(string value) => NotifyConfigChanged();

    public override string Apply(string currentName, int index, int totalCount, string originalPath, bool isDirectory)
    {
        string namePart = isDirectory ? currentName : Path.GetFileNameWithoutExtension(currentName);
        string ext = isDirectory ? string.Empty : Path.GetExtension(currentName);

        int num = StartNumber + (index * Step);
        string numStr = ZeroPadding > 0 ? num.ToString().PadLeft(ZeroPadding, '0') : num.ToString();

        string newNamePart;
        if (Location == "Prepend")
        {
            newNamePart = $"{numStr}{Separator}{namePart}";
        }
        else if (Location == "Append")
        {
            newNamePart = $"{namePart}{Separator}{numStr}";
        }
        else // "Replace"
        {
            Match m = Regex.Match(namePart, @"\d+");
            if (m.Success)
            {
                newNamePart = namePart.Remove(m.Index, m.Length).Insert(m.Index, numStr);
            }
            else
            {
                newNamePart = numStr;
            }
        }

        return isDirectory || string.IsNullOrEmpty(ext) ? newNamePart : $"{newNamePart}{ext}";
    }
}

// 4. Change Case Method
public partial class ChangeCaseMethod : RenameMethodBase
{
    [ObservableProperty]
    private string _caseType = "Title Case"; // "lowercase", "UPPERCASE", "Title Case", "Sentence case"

    public ChangeCaseMethod()
    {
        Name = "Đổi hoa/thường (Case)";
        Icon = "🔤";
    }

    partial void OnCaseTypeChanged(string value) => NotifyConfigChanged();

    public override string Apply(string currentName, int index, int totalCount, string originalPath, bool isDirectory)
    {
        string namePart = isDirectory ? currentName : Path.GetFileNameWithoutExtension(currentName);
        string ext = isDirectory ? string.Empty : Path.GetExtension(currentName);
        if (ext.StartsWith(".")) ext = ext.Substring(1);

        string target = isDirectory || ApplyTo == "Both" ? currentName : (ApplyTo == "Extension" ? ext : namePart);
        string result = target;

        switch (CaseType)
        {
            case "lowercase":
                result = target.ToLowerInvariant();
                break;
            case "UPPERCASE":
                result = target.ToUpperInvariant();
                break;
            case "Title Case":
                result = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(target.ToLower());
                break;
            case "Sentence case":
                if (target.Length > 0)
                {
                    result = char.ToUpperInvariant(target[0]) + (target.Length > 1 ? target.Substring(1).ToLowerInvariant() : "");
                }
                break;
        }

        if (isDirectory || ApplyTo == "Both") return result;

        if (ApplyTo == "Name")
        {
            return string.IsNullOrEmpty(ext) ? result : $"{result}.{ext}";
        }
        else
        {
            return string.IsNullOrEmpty(result) ? namePart : $"{namePart}.{result}";
        }
    }
}

// 5. Remove Method
public partial class RemoveMethod : RenameMethodBase
{
    [ObservableProperty]
    private string _removeType = "String"; // "String", "Range", "ExtraWhitespace", "SpecialChars", "Digits"

    [ObservableProperty]
    private string _targetText = string.Empty;

    [ObservableProperty]
    private int _fromPosition = 1;

    [ObservableProperty]
    private int _count = 1;

    public RemoveMethod()
    {
        Name = "Xóa ký tự (Remove)";
        Icon = "✂️";
    }

    partial void OnRemoveTypeChanged(string value) => NotifyConfigChanged();
    partial void OnTargetTextChanged(string value) => NotifyConfigChanged();
    partial void OnFromPositionChanged(int value) => NotifyConfigChanged();
    partial void OnCountChanged(int value) => NotifyConfigChanged();

    public override string Apply(string currentName, int index, int totalCount, string originalPath, bool isDirectory)
    {
        string namePart = isDirectory ? currentName : Path.GetFileNameWithoutExtension(currentName);
        string ext = isDirectory ? string.Empty : Path.GetExtension(currentName);
        if (ext.StartsWith(".")) ext = ext.Substring(1);

        string target = isDirectory || ApplyTo == "Both" ? currentName : (ApplyTo == "Extension" ? ext : namePart);
        string result = target;

        if (RemoveType == "String" && !string.IsNullOrEmpty(TargetText))
        {
            result = target.Replace(TargetText, string.Empty);
        }
        else if (RemoveType == "Range")
        {
            int startIdx = Math.Max(0, FromPosition - 1);
            if (startIdx < target.Length)
            {
                int len = Math.Min(Count, target.Length - startIdx);
                result = target.Remove(startIdx, len);
            }
        }
        else if (RemoveType == "ExtraWhitespace")
        {
            result = Regex.Replace(target.Trim(), @"\s+", " ");
        }
        else if (RemoveType == "SpecialChars")
        {
            result = Regex.Replace(target, @"[^\w\s.-]", "");
        }
        else if (RemoveType == "Digits")
        {
            result = Regex.Replace(target, @"\d+", "");
        }

        if (isDirectory || ApplyTo == "Both") return result;

        if (ApplyTo == "Name")
        {
            return string.IsNullOrEmpty(ext) ? result : $"{result}.{ext}";
        }
        else
        {
            return string.IsNullOrEmpty(result) ? namePart : $"{namePart}.{result}";
        }
    }
}

// 6. Optimize Zero Method
public partial class OptimizeZeroMethod : RenameMethodBase
{
    [ObservableProperty]
    private int _padLength = 3;

    public OptimizeZeroMethod()
    {
        Name = "Chuẩn hóa số 0 (Zero Pad)";
        Icon = "0️⃣";
    }

    partial void OnPadLengthChanged(int value) => NotifyConfigChanged();

    public override string Apply(string currentName, int index, int totalCount, string originalPath, bool isDirectory)
    {
        string namePart = isDirectory ? currentName : Path.GetFileNameWithoutExtension(currentName);
        string ext = isDirectory ? string.Empty : Path.GetExtension(currentName);

        string result = Regex.Replace(namePart, @"\d+", m =>
        {
            string numStr = m.Value;
            if (numStr.Length < PadLength)
            {
                return numStr.PadLeft(PadLength, '0');
            }
            return numStr;
        });

        return isDirectory || string.IsNullOrEmpty(ext) ? result : $"{result}{ext}";
    }
}

public class BatchRenameService
{
    private static readonly Lazy<BatchRenameService> _instance = new(() => new BatchRenameService());
    public static BatchRenameService Instance => _instance.Value;

    private readonly List<(string OldPath, string NewPath)> _lastUndoHistory = new();
    public bool CanUndo => _lastUndoHistory.Count > 0;

    public void UpdatePreview(IList<RenameItem> items, IList<RenameMethodBase> methods)
    {
        if (items == null || items.Count == 0) return;

        var activeMethods = methods?.Where(m => m.IsEnabled).ToList() ?? new List<RenameMethodBase>();
        int total = items.Count;

        for (int i = 0; i < total; i++)
        {
            var item = items[i];
            string current = item.OriginalName;

            foreach (var method in activeMethods)
            {
                try
                {
                    current = method.Apply(current, i, total, item.OriginalPath, item.IsDirectory);
                }
                catch { }
            }

            item.NewName = current;
            ValidateItem(item);
        }

        // Kiểm tra trùng lặp tên giữa các item cùng thư mục cha
        var duplicates = items
            .GroupBy(it => Path.Combine(it.DirectoryPath, it.NewName), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in duplicates)
        {
            foreach (var it in group)
            {
                it.IsValid = false;
                it.StatusText = "⚠️ Trùng tên mới với item khác";
            }
        }
    }

    private static void ValidateItem(RenameItem item)
    {
        if (string.IsNullOrWhiteSpace(item.NewName))
        {
            item.IsValid = false;
            item.StatusText = "❌ Tên mới không được để trống";
            return;
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        if (item.NewName.IndexOfAny(invalidChars) >= 0)
        {
            item.IsValid = false;
            item.StatusText = "❌ Chứa ký tự cấm của hệ điều hành";
            return;
        }

        if (!item.IsChanged)
        {
            item.IsValid = true;
            item.StatusText = "— Không đổi";
            return;
        }

        item.IsValid = true;
        item.StatusText = "✓ Hợp lệ";
    }

    public async Task<(int Succeeded, int Failed, string Message)> ExecuteBatchAsync(IList<RenameItem> items, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            _lastUndoHistory.Clear();
            if (items == null || items.Count == 0)
            {
                return (0, 0, "Không có mục nào để đổi tên.");
            }

            var toRename = items.Where(it => it.IsChanged && it.IsValid).ToList();
            if (toRename.Count == 0)
            {
                return (0, 0, "Không có mục nào hợp lệ cần đổi tên.");
            }

            int succeeded = 0;
            int failed = 0;

            // Pha 1: Đổi sang tên tạm GUID an toàn (chống xung đột hoặc case-insensitive rename trên Windows)
            var intermediateList = new List<(RenameItem Item, string TempPath, string FinalPath)>();

            foreach (var it in toRename)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    string dir = it.DirectoryPath;
                    string tempName = $"__gmtpc_temp_{Guid.NewGuid():N}";
                    string tempPath = Path.Combine(dir, tempName);
                    string finalPath = Path.Combine(dir, it.NewName);

                    if (it.IsDirectory)
                    {
                        if (Directory.Exists(it.OriginalPath))
                        {
                            Directory.Move(it.OriginalPath, tempPath);
                            intermediateList.Add((it, tempPath, finalPath));
                        }
                    }
                    else
                    {
                        if (File.Exists(it.OriginalPath))
                        {
                            File.Move(it.OriginalPath, tempPath);
                            intermediateList.Add((it, tempPath, finalPath));
                        }
                    }
                }
                catch
                {
                    failed++;
                }
            }

            // Pha 2: Đổi từ tên tạm sang tên thật NewName
            foreach (var tuple in intermediateList)
            {
                try
                {
                    if (tuple.Item.IsDirectory)
                    {
                        Directory.Move(tuple.TempPath, tuple.FinalPath);
                    }
                    else
                    {
                        File.Move(tuple.TempPath, tuple.FinalPath);
                    }

                    _lastUndoHistory.Add((tuple.Item.OriginalPath, tuple.FinalPath));
                    tuple.Item.OriginalPath = tuple.FinalPath; // Cập nhật path mới
                    tuple.Item.StatusText = "✓ Đã đổi tên";
                    succeeded++;
                }
                catch
                {
                    failed++;
                    try
                    {
                        if (tuple.Item.IsDirectory) Directory.Move(tuple.TempPath, tuple.Item.OriginalPath);
                        else File.Move(tuple.TempPath, tuple.Item.OriginalPath);
                    }
                    catch { }
                }
            }

            return (succeeded, failed, $"Hoàn thành đổi tên: {succeeded} thành công, {failed} thất bại.");
        }, ct);
    }

    public async Task<(int Restored, string Message)> UndoBatchAsync()
    {
        return await Task.Run(() =>
        {
            if (_lastUndoHistory.Count == 0) return (0, "Không có lịch sử để hoàn tác.");

            int restored = 0;
            for (int i = _lastUndoHistory.Count - 1; i >= 0; i--)
            {
                var (oldPath, newPath) = _lastUndoHistory[i];
                try
                {
                    if (Directory.Exists(newPath))
                    {
                        Directory.Move(newPath, oldPath);
                        restored++;
                    }
                    else if (File.Exists(newPath))
                    {
                        File.Move(newPath, oldPath);
                        restored++;
                    }
                }
                catch { }
            }

            _lastUndoHistory.Clear();
            return (restored, $"Đã hoàn tác thành công {restored} mục trở về tên ban đầu.");
        });
    }
}
