using Humanizer;
using NexusMods.App.UI.Controls.Filters;
using NexusMods.App.UI.Controls.TreeDataGrid.Filters;
using NexusMods.App.UI.Resources;
using NexusMods.Paths;
using NexusMods.UI.Sdk;
using R3;

namespace NexusMods.App.UI.Controls;

/// <summary>
/// Shared progress components used by both regular downloads and collection downloads.
/// </summary>
public static class SharedProgressComponents
{
    /// <summary>
    /// Displays download size progress: "15.6MB of 56MB" (downloaded of total).
    /// </summary>
    public sealed class SizeProgressComponent : ReactiveR3Object, IItemModelComponent<SizeProgressComponent>, IComparable<SizeProgressComponent>
    {
        public IReadOnlyBindableReactiveProperty<string> DisplayText { get; }
        public IReadOnlyBindableReactiveProperty<Size> DownloadedBytes { get; }
        public IReadOnlyBindableReactiveProperty<Size> TotalSize { get; }

        public SizeProgressComponent(
            Size initialDownloaded,
            Size initialTotal,
            Observable<Size> downloadedObservable,
            Observable<Size> totalObservable)
        {
            DownloadedBytes = downloadedObservable.ToBindableReactiveProperty(initialDownloaded);
            TotalSize = totalObservable.ToBindableReactiveProperty(initialTotal);
            DisplayText = downloadedObservable
                .CombineLatest(totalObservable, FormatSizeProgress)
                .ToBindableReactiveProperty(FormatSizeProgress(initialDownloaded, initialTotal));
        }

        public int CompareTo(SizeProgressComponent? other)
        {
            if (other is null) return 1;
            return TotalSize.Value.CompareTo(other.TotalSize.Value);
        }

        public FilterResult MatchesFilter(Filter filter)
        {
            return filter switch
            {
                Filter.SizeRangeFilter sizeFilter =>
                    (TotalSize.Value >= sizeFilter.MinSize && TotalSize.Value <= sizeFilter.MaxSize)
                    ? FilterResult.Pass : FilterResult.Fail,
                Filter.TextFilter textFilter => DisplayText.Value.Contains(
                    textFilter.SearchText,
                    textFilter.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
                    ? FilterResult.Pass : FilterResult.Fail,
                _ => FilterResult.Indeterminate,
            };
        }

        public static string FormatSizeProgress(Size downloaded, Size total)
        {
            var downloadedSize = ByteSize.FromBytes(downloaded.Value);
            var totalSize = ByteSize.FromBytes(total.Value);

            var downloadedStr = downloadedSize.Gigabytes < 1 ? downloadedSize.Humanize("0") : downloadedSize.Humanize("0.0");
            var totalStr = totalSize.Gigabytes < 1 ? totalSize.Humanize("0") : totalSize.Humanize("0.0");

            return $"{downloadedStr}{Language.Downloads_SizeProgress_Of}{totalStr}";
        }

        private bool _isDisposed;
        protected override void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                    Disposable.Dispose(DisplayText, DownloadedBytes, TotalSize);

                _isDisposed = true;
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Displays transfer speed: "5.2 MB/s" or "--" when inactive.
    /// </summary>
    public sealed class SpeedComponent : ReactiveR3Object, IItemModelComponent<SpeedComponent>, IComparable<SpeedComponent>
    {
        public IReadOnlyBindableReactiveProperty<Size> TransferRate { get; }
        public IReadOnlyBindableReactiveProperty<string> DisplayText { get; }

        public SpeedComponent(
            Size initialTransferRate,
            Observable<Size> transferRateObservable)
        {
            TransferRate = transferRateObservable.ToBindableReactiveProperty(initialTransferRate);
            DisplayText = transferRateObservable
                .Select(FormatTransferRate)
                .ToBindableReactiveProperty(FormatTransferRate(initialTransferRate));
        }

        public int CompareTo(SpeedComponent? other)
        {
            if (other is null) return 1;
            return TransferRate.Value.CompareTo(other.TransferRate.Value);
        }

        public FilterResult MatchesFilter(Filter filter)
        {
            return filter switch
            {
                Filter.SizeRangeFilter sizeFilter =>
                    (TransferRate.Value >= sizeFilter.MinSize && TransferRate.Value <= sizeFilter.MaxSize)
                    ? FilterResult.Pass : FilterResult.Fail,
                Filter.TextFilter textFilter => DisplayText.Value.Contains(
                    textFilter.SearchText,
                    textFilter.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
                    ? FilterResult.Pass : FilterResult.Fail,
                _ => FilterResult.Indeterminate,
            };
        }

        public static string FormatTransferRate(Size rate)
        {
            if (rate.Value <= 0) return Language.Downloads_Speed_Inactive;
            return new ByteRate(ByteSize.FromBytes(rate.Value), TimeSpan.FromSeconds(1)).Humanize();
        }

        private bool _isDisposed;
        protected override void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                    Disposable.Dispose(TransferRate, DisplayText);

                _isDisposed = true;
            }
            base.Dispose(disposing);
        }
    }
}
