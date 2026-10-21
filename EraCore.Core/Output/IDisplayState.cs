namespace MinorShift.Emuera.Output;

internal interface IDisplayState
{
    bool TryUpdate();
    DisplaySnapshot Current { get; }
}
