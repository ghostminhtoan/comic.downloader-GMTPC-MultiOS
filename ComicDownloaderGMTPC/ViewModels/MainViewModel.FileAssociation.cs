using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ComicDownloaderGMTPC.Services;

namespace ComicDownloaderGMTPC.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DefaultImageBookStatusText))]
    [NotifyPropertyChangedFor(nameof(DefaultImageBookButtonText))]
    [NotifyPropertyChangedFor(nameof(DefaultImageBookButtonBackground))]
    private bool _isDefaultImageBookAssociated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArchiveContextMenuStatusText))]
    [NotifyPropertyChangedFor(nameof(ArchiveContextMenuButtonText))]
    [NotifyPropertyChangedFor(nameof(ArchiveContextMenuButtonBackground))]
    private bool _isArchiveContextMenuAssociated;

    public string DefaultImageBookStatusText =>
        IsDefaultImageBookAssociated ? "✅ Đang BẬT (Double-click mở thẳng bằng app)" : "⚪ Đang TẮT (Dùng ứng dụng mặc định khác)";

    public string DefaultImageBookButtonText =>
        IsDefaultImageBookAssociated ? "TẮT MẶC ĐỊNH" : "BẬT MẶC ĐỊNH";

    public string DefaultImageBookButtonBackground =>
        IsDefaultImageBookAssociated ? "#0284C7" : "#334155";

    public string ArchiveContextMenuStatusText =>
        IsArchiveContextMenuAssociated ? "✅ Đang BẬT (Menu chuột phải cho Thư mục & tệp nén ZIP, RAR, CBZ, CBR)" : "⚪ Đang TẮT (Chưa thêm vào menu chuột phải)";

    public string ArchiveContextMenuButtonText =>
        IsArchiveContextMenuAssociated ? "TẮT MENU PHẢI" : "BẬT MENU PHẢI";

    public string ArchiveContextMenuButtonBackground =>
        IsArchiveContextMenuAssociated ? "#059669" : "#334155";


    [ObservableProperty]
    private string _associationStatusMessage = "Sẵn sàng thiết lập liên kết tệp.";

    [ObservableProperty]
    private string _associationPlatformText = string.Empty;

    [ObservableProperty]
    private bool _isAssociationPlatformSupported;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MultiInstanceStatusText))]
    private bool _isMultiInstanceEnabled = InstanceConfigService.Instance.IsMultiInstanceEnabled;

    public string MultiInstanceStatusText =>
        IsMultiInstanceEnabled
            ? "⚡ Đang BẬT: Cho phép mở nhiều cửa sổ App chính song song"
            : "🔒 Đang TẮT (Khuyên dùng): Chỉ chạy 1 cửa sổ App chính để tránh xung đột tải và bảo vệ dữ liệu";

    partial void OnIsMultiInstanceEnabledChanged(bool value)
    {
        InstanceConfigService.Instance.IsMultiInstanceEnabled = value;
    }

    /// <summary>
    /// Khởi tạo và quét trạng thái liên kết tệp hiện tại từ hệ điều hành.
    /// </summary>
    public void InitFileAssociation()
    {
        try
        {
            var service = FileAssociationService.Instance;
            IsAssociationPlatformSupported = service.IsSupportedPlatform;
            AssociationPlatformText = service.PlatformDescription;

            if (IsAssociationPlatformSupported)
            {
                IsDefaultImageBookAssociated = service.CheckDefaultImageBookRegistered();
                IsArchiveContextMenuAssociated = service.CheckArchiveContextMenuRegistered();
                AssociationStatusMessage = (IsDefaultImageBookAssociated || IsArchiveContextMenuAssociated)
                    ? "ℹ️ Đã nhận diện cấu hình liên kết tệp hiện hữu trên hệ thống."
                    : "ℹ️ Hiện tại app chưa được đặt làm mặc định hoặc thêm menu chuột phải.";
            }
            else
            {
                AssociationStatusMessage = "⚠️ Tính năng liên kết tệp hệ thống chỉ áp dụng cho Windows và Linux.";
            }
        }
        catch (Exception ex)
        {
            AssociationStatusMessage = $"⚠️ Lỗi kiểm tra trạng thái liên kết tệp: {ex.Message}";
        }
    }

    [RelayCommand]
    public void RefreshAssociationStatus()
    {
        InitFileAssociation();
    }

    [RelayCommand]
    public async Task ToggleDefaultImageBookAssociationAsync()
    {
        if (!FileAssociationService.Instance.IsSupportedPlatform)
        {
            AssociationStatusMessage = "⚠️ Tính năng chỉ hỗ trợ trên Windows và Linux.";
            return;
        }

        bool targetState = !IsDefaultImageBookAssociated;
        AssociationStatusMessage = targetState
            ? "⏳ Đang thiết lập mở mặc định cho các tệp ảnh và sách truyện..."
            : "⏳ Đang gỡ bỏ mở mặc định cho các tệp ảnh và sách truyện...";

        var (success, msg) = await Task.Run(() =>
        {
            return targetState
                ? FileAssociationService.Instance.RegisterDefaultImageAndBookAssociations()
                : FileAssociationService.Instance.UnregisterDefaultImageAndBookAssociations();
        });

        IsDefaultImageBookAssociated = FileAssociationService.Instance.CheckDefaultImageBookRegistered();
        AssociationStatusMessage = msg;
    }

    [RelayCommand]
    public async Task ToggleArchiveContextMenuAsync()
    {
        if (!FileAssociationService.Instance.IsSupportedPlatform)
        {
            AssociationStatusMessage = "⚠️ Tính năng chỉ hỗ trợ trên Windows và Linux.";
            return;
        }

        bool targetState = !IsArchiveContextMenuAssociated;
        AssociationStatusMessage = targetState
            ? "⏳ Đang thêm menu chuột phải cho các tệp nén..."
            : "⏳ Đang gỡ bỏ menu chuột phải cho các tệp nén...";

        var (success, msg) = await Task.Run(() =>
        {
            return targetState
                ? FileAssociationService.Instance.RegisterArchiveContextMenu()
                : FileAssociationService.Instance.UnregisterArchiveContextMenu();
        });

        IsArchiveContextMenuAssociated = FileAssociationService.Instance.CheckArchiveContextMenuRegistered();
        AssociationStatusMessage = msg;
    }

    [RelayCommand]
    public async Task RegisterAllAssociationsAsync()
    {
        if (!FileAssociationService.Instance.IsSupportedPlatform)
        {
            AssociationStatusMessage = "⚠️ Tính năng chỉ hỗ trợ trên Windows và Linux.";
            return;
        }

        AssociationStatusMessage = "⏳ Đang kích hoạt toàn bộ liên kết mở mặc định và menu chuột phải...";

        var (success, msg) = await Task.Run(() => FileAssociationService.Instance.RegisterAll());

        IsDefaultImageBookAssociated = FileAssociationService.Instance.CheckDefaultImageBookRegistered();
        IsArchiveContextMenuAssociated = FileAssociationService.Instance.CheckArchiveContextMenuRegistered();
        AssociationStatusMessage = msg;
    }

    [RelayCommand]
    public async Task UnregisterAllAssociationsAsync()
    {
        if (!FileAssociationService.Instance.IsSupportedPlatform)
        {
            AssociationStatusMessage = "⚠️ Tính năng chỉ hỗ trợ trên Windows và Linux.";
            return;
        }

        AssociationStatusMessage = "⏳ Đang gỡ bỏ toàn bộ liên kết tệp và menu chuột phải...";

        var (success, msg) = await Task.Run(() => FileAssociationService.Instance.UnregisterAll());

        IsDefaultImageBookAssociated = FileAssociationService.Instance.CheckDefaultImageBookRegistered();
        IsArchiveContextMenuAssociated = FileAssociationService.Instance.CheckArchiveContextMenuRegistered();
        AssociationStatusMessage = msg;
    }
}

