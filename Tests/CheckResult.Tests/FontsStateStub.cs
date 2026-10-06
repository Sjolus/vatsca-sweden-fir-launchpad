namespace VatscaUpdateChecker.Services;

// The presentation model needs only the state; this harness never queries installed fonts.
public enum FontsState { Unknown, AllOk, NeedsAction }
