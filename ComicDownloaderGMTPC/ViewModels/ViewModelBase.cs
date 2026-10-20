// ====================================================================================================
// FILE SUMMARY: ViewModelBase.cs
// ----------------------------------------------------------------------------------------------------
// VAI TRÒ & MỤC ĐÍCH:
//   Lớp cơ sở (Base Class) cho toàn bộ các ViewModel trong kiến trúc MVVM của Comic Downloader GMTPC.
//
// CÁC TÍNH NĂNG & THÀNH PHẦN CHÍNH:
//   - Kế thừa ObservableObject của CommunityToolkit.Mvvm: Cung cấp khả năng thông báo thay đổi giao diện (INotifyPropertyChanged).
//   - Cung cấp các tiện ích dùng chung cho quản lý trạng thái, Dispatcher và xử lý sự kiện giao diện.
//
// LƯU Ý KỸ THUẬT & QUY TẮC BẢO TRÌ:
//   - Mọi ViewModel trong ứng dụng bắt buộc kế thừa từ lớp này để duy trì tính nhất quán kiến trúc.
// ====================================================================================================

using CommunityToolkit.Mvvm.ComponentModel;

namespace ComicDownloaderGMTPC.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
}
