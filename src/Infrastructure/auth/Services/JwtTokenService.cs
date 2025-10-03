using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Auth.Infrastructure.Services;

public class JwtTokenService : IJwtTokenService
{
    private readonly JwtOptions _opt;
    private readonly SymmetricSecurityKey _key;
    public JwtTokenService(IOptions<JwtOptions> options)
    {
        _opt = options.Value; //inyección de la configuración 
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.Key)); //Creación de simetria para validar tokens
    }


    //cadena de creación de los tokens
    public (string token, string jti) CreateToken(IEnumerable<Claim> claims)
    {
        var jti = Guid.NewGuid().ToString("N"); //creación del identificador unico
        var now = DateTime.UtcNow; //Tiempo base

        var token = new JwtSecurityToken(
            issuer: _opt.Issuer,
            audience: _opt.Audience,
            claims: claims.Append(new Claim(JwtRegisteredClaimNames.Jti, jti)),
            notBefore: now,
            expires: now.AddMinutes(_opt.AccessTokenMinutes), //cadeca tras AccessTokenMinutes
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256)
        );

        var jwt = new JwtSecurityTokenHandler().WriteToken(token); //serializa el JWT a string
        return (jwt, jti);
    }

    public string ComputeSha256(string input)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes); // hex
    }
}
