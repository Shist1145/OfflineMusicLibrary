using System.ComponentModel;

namespace OfflineMusicLibrary;

public sealed class LyricLine : INotifyPropertyChanged
{
	private bool _isCurrent;

	public long TimeMs { get; init; }

	public string Original { get; init; } = "";

	public string Romanization { get; init; } = "";

	public string Translation { get; set; } = "";

	public bool IsCurrent
	{
		get
		{
			return _isCurrent;
		}
		set
		{
			if (_isCurrent != value)
			{
				_isCurrent = value;
				this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsCurrent"));
			}
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;
}
