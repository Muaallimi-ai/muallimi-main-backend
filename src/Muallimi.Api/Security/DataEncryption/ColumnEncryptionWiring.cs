using Microsoft.Extensions.DependencyInjection;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Security.DataEncryption;

/// <summary>
/// T090 — Wires the column-encryption static adapter in
/// <see cref="ColumnEncryption"/> to the concrete
/// <see cref="IDataEncryptionAdapter"/> resolved from DI. Called once at
/// application startup after the service provider is built.
/// </summary>
public static class ColumnEncryptionWiring
{
    public static void UsePhase6ColumnEncryption(this IApplicationBuilder app)
    {
        var adapter = app.ApplicationServices.GetRequiredService<IDataEncryptionAdapter>();
        ColumnEncryption.Encrypt = plaintext => adapter.Encrypt(plaintext);
        ColumnEncryption.Decrypt = ciphertext => adapter.Decrypt(ciphertext);
    }
}
