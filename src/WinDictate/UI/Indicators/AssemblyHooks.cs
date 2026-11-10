using System.Runtime.CompilerServices;

// The Windows-only smoke tests open the real indicator windows and need the
// palettes and the test hook on IndicatorTheme.
[assembly: InternalsVisibleTo("WinDictate.UiSmoke")]
