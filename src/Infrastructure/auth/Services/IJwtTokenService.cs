using System.Security.Claims;

namespace Auth.Infrastructure.Services;

public interface IJwtTokenService
{
    /*
    Entrada: una lista de claims (datos dentro del JWT: sub, name, email, roles, etc.).

    Salida: una tupla (token, jti)

    token: el JWT firmado (string) que devuelves al cliente.

    jti: el ID único del token (claim estándar jti), útil para registrar la sesión, revocar tokens, auditoría, etc.
    */
    (string token, string jti) CreateToken(IEnumerable<Claim> claims); 

    /*

    Entrada: un texto (normalmente el JWT recién emitido).

    Salida: el hash SHA-256 en string (hex).
    */
    string ComputeSha256(string input); // para hash del token
}
