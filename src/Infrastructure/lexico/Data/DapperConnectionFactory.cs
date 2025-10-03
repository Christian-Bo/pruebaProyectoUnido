using System.Data;
using Microsoft.Extensions.Configuration;
using MySqlConnector;

namespace Lexico.Infrastructure.Data;

/// <summary>
/// Crea conexiones IDbConnection leyendo variables de entorno de Railway.
/// Prioriza una cadena MySQL completa si existe; si no, arma desde MYSQLHOST, etc.
/// </summary>
public class DapperConnectionFactory
{
    private readonly string _conn;

    public DapperConnectionFactory(IConfiguration cfg)
    {
        // 1) Si existe ConnectionStrings:MySQLConnection, usarla
        var direct = cfg.GetConnectionString("MySQLConnection");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            _conn = direct;
            return;
        }

        // 2) Armar desde variables típicas de Railway
        var host = Environment.GetEnvironmentVariable("MYSQLHOST");
        var db   = Environment.GetEnvironmentVariable("MYSQLDATABASE");
        var user = Environment.GetEnvironmentVariable("MYSQLUSER");
        var pass = Environment.GetEnvironmentVariable("MYSQLPASSWORD");
        var port = Environment.GetEnvironmentVariable("MYSQLPORT") ?? "3306";

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(db) || string.IsNullOrEmpty(user))
            throw new InvalidOperationException("No hay cadena de conexión MySQL válida (ni variables MYSQL*).");

        _conn = $"Server={host};Port={port};Database={db};User ID={user};Password={pass};SslMode=None;Allow User Variables=True;Connection Timeout=30;";
    }

    public IDbConnection Create() => new MySqlConnection(_conn);
}
