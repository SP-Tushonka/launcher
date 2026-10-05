namespace SPTarkov.Core.Helpers;

public interface IClipboard
{
    bool CopyText(string text);
    void CopyFiles(string[] files);
}