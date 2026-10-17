using System;
using System.Collections.Generic;
using System.Linq;

namespace ComicDownloaderGMTPC.Services;

/// <summary>
/// Bộ so sánh chuỗi theo thứ tự tự nhiên (Natural / Alphanumeric Sort Comparer)
/// Hỗ trợ sắp xếp chính xác: 1, 2, 3, ..., 9, 10, 11, 12, ... 19, 20, 21, ...
/// Chuẩn hóa toàn diện trên đa nền tảng (Windows, Linux, Android), không phân biệt hoa thường.
/// </summary>
public class NaturalSortComparer : IComparer<string>
{
    public static readonly NaturalSortComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        int ix = 0, iy = 0;
        int lenX = x.Length, lenY = y.Length;

        while (ix < lenX && iy < lenY)
        {
            char cx = x[ix];
            char cy = y[iy];

            if (char.IsDigit(cx) && char.IsDigit(cy))
            {
                // Bỏ qua các số 0 ở đầu để tính độ dài giá trị thực của số
                int startX = ix, startY = iy;
                while (ix < lenX && x[ix] == '0') ix++;
                while (iy < lenY && y[iy] == '0') iy++;

                int numStartX = ix, numStartY = iy;
                while (ix < lenX && char.IsDigit(x[ix])) ix++;
                while (iy < lenY && char.IsDigit(y[iy])) iy++;

                int numLenX = ix - numStartX;
                int numLenY = iy - numStartY;

                // Nếu độ dài phần số khác nhau, chuỗi có phần số dài hơn là số lớn hơn (ví dụ 10 dài hơn 2)
                if (numLenX != numLenY)
                {
                    return numLenX.CompareTo(numLenY);
                }

                // Nếu cùng độ dài, so sánh từng chữ số
                for (int i = 0; i < numLenX; i++)
                {
                    int digitCompare = x[numStartX + i].CompareTo(y[numStartY + i]);
                    if (digitCompare != 0)
                    {
                        return digitCompare;
                    }
                }

                // Nếu giá trị số bằng nhau, chuỗi có ít số 0 ở đầu hơn sẽ xếp trước (ví dụ "1" trước "01")
                int leadingZerosX = numStartX - startX;
                int leadingZerosY = numStartY - startY;
                if (leadingZerosX != leadingZerosY)
                {
                    return leadingZerosX.CompareTo(leadingZerosY);
                }
            }
            else
            {
                int wx = GetCharCategoryWeight(cx);
                int wy = GetCharCategoryWeight(cy);
                if (wx != wy)
                {
                    return wx.CompareTo(wy);
                }

                int charCompare = char.ToUpperInvariant(cx).CompareTo(char.ToUpperInvariant(cy));
                if (charCompare != 0)
                {
                    return charCompare;
                }
                ix++;
                iy++;
            }
        }

        return lenX.CompareTo(lenY);
    }

    /// <summary>
    /// Trọng số phân lớp ký tự:
    /// 0: Khoảng trắng
    /// 1: Ký tự đặc biệt / dấu ngoặc / ký hiệu: [, ], (, ), _, -, #, !, @... (Luôn xếp TRƯỚC chữ cái A-Z và chữ số)
    /// 2: Chữ số 0-9
    /// 3: Chữ cái A-Z
    /// </summary>
    private static int GetCharCategoryWeight(char c)
    {
        if (char.IsWhiteSpace(c)) return 0;
        if (!char.IsLetterOrDigit(c)) return 1;
        if (char.IsDigit(c)) return 2;
        return 3;
    }
}

public static class NaturalSortExtensions
{
    /// <summary>
    /// Sắp xếp danh sách chuỗi / đường dẫn theo thứ tự tự nhiên của con người (1, 2, ..., 9, 10 thay vì 1, 10, 2).
    /// </summary>
    public static List<string> NaturalSort(this IEnumerable<string> items)
    {
        return items.OrderBy(x => x, NaturalSortComparer.Instance).ToList();
    }
}
