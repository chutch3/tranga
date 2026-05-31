using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace API.Schema.SeriesContext;

[PrimaryKey("Key")]
public class FileLibrary(string basePath, string libraryName)
    : Identifiable(TokenGen.CreateToken(typeof(FileLibrary), basePath.Trim()))
{
    private string _basePath = basePath.Trim();

    [StringLength(256)] 
    public string BasePath 
    { 
        get => _basePath; 
        internal set => _basePath = value.Trim(); 
    }

    [StringLength(512)] public string LibraryName { get; internal set; } = libraryName;

    public override string ToString() => $"{base.ToString()} {LibraryName} - {BasePath}";
}
