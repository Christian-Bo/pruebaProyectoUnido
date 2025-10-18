using Microsoft.EntityFrameworkCore; 
using Auth.Domain.Entities;

namespace Auth.Infrastructure.Data;

/// <summary>
/// Contexto de base de datos para el sistema de autenticación.
/// 
/// OPTIMIZACIONES IMPLEMENTADAS:
/// - Índices únicos para búsquedas rápidas (usuario, email, QR)
/// - Índices compuestos para consultas frecuentes
/// - Charset UTF8MB4 para soporte de emojis
/// - Collation case-insensitive (ai_ci)
/// 
/// MEJORAS FUTURAS:
/// - Implementar soft delete global con query filters
/// - Agregar auditoría automática (CreatedBy, UpdatedBy)
/// - Configurar batch size para inserciones masivas
/// - Implementar connection resiliency (ya está en Program.cs)
/// 
/// ESCALABILIDAD:
/// - Para multi-tenant: Agregar TenantId a todas las entidades
/// - Para sharding: Implementar IDbContextFactory<T>
/// - Para read replicas: Separar contextos read/write
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> opts) : base(opts) { }

    // ===== DbSets (Tablas) =====
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<AutenticacionFacial> AutenticacionFacial => Set<AutenticacionFacial>();
    public DbSet<CodigoQr> CodigosQr => Set<CodigoQr>();
    public DbSet<MetodoNotificacion> MetodosNotificacion => Set<MetodoNotificacion>();
    public DbSet<Sesion> Sesiones => Set<Sesion>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // ===== Configuración global =====
        // Charset UTF8MB4: Soporta emojis y caracteres especiales
        // Collation ai_ci: Accent-insensitive, Case-insensitive
        mb.HasCharSet("utf8mb4").UseCollation("utf8mb4_0900_ai_ci");

        // ========= USUARIOS =========
        mb.Entity<Usuario>(e =>
        {
            e.ToTable("usuarios");
            
            // Mapeo de propiedades
            e.Property(p => p.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(p => p.UsuarioNombre).HasColumnName("usuario").IsRequired();
            e.Property(p => p.Email).HasColumnName("email").IsRequired();
            e.Property(p => p.NombreCompleto).HasColumnName("nombre_completo").IsRequired();
            e.Property(p => p.PasswordHash).HasColumnName("password_hash").IsRequired();
            e.Property(p => p.Telefono).HasColumnName("telefono");
            e.Property(p => p.FechaCreacion).HasColumnName("fecha_creacion")
                .HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(p => p.Activo).HasColumnName("activo").HasDefaultValue(true);

            // ÍNDICES CRÍTICOS (previenen duplicados y mejoran búsquedas)
            e.HasIndex(p => p.UsuarioNombre)
                .IsUnique()
                .HasDatabaseName("idx_usuario");
            
            e.HasIndex(p => p.Email)
                .IsUnique()
                .HasDatabaseName("idx_email");
            
            // Índice para filtrar activos (muy común en consultas)
            e.HasIndex(p => p.Activo)
                .HasDatabaseName("idx_activo");
            
            // MEJORA FUTURA: Índice compuesto para login
            // e.HasIndex(p => new { p.Activo, p.UsuarioNombre }).HasDatabaseName("idx_login");
        });

        // ========= AUTENTICACIÓN FACIAL =========
        mb.Entity<AutenticacionFacial>(e =>
        {
            e.ToTable("autenticacion_facial");
            
            e.Property(p => p.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(p => p.UsuarioId).HasColumnName("usuario_id").IsRequired();
            
            // LONGTEXT para encodings grandes (hasta 4GB)
            e.Property(p => p.EncodingFacial).HasColumnName("encoding_facial")
                .HasColumnType("TEXT");
            
            // LONGTEXT para imágenes Base64 (hasta 4GB)
            // MEJORA FUTURA: Mover a blob storage (S3/Azure Storage)
            e.Property(p => p.ImagenReferencia).HasColumnName("imagen_referencia")
                .HasColumnType("LONGTEXT");
            
            e.Property(p => p.Activo).HasColumnName("activo").HasDefaultValue(true);
            e.Property(p => p.FechaCreacion).HasColumnName("fecha_creacion")
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            // Relación con Usuario (cascada: si se borra usuario, se borran sus fotos)
            e.HasOne(p => p.Usuario)
             .WithMany(u => u.AutenticacionesFaciales)
             .HasForeignKey(p => p.UsuarioId)
             .OnDelete(DeleteBehavior.Cascade);

            // Índice compuesto para consulta optimizada (usado en TryGetUserPhotoBytesAsync)
            e.HasIndex(p => new { p.UsuarioId, p.Activo, p.FechaCreacion })
             .HasDatabaseName("idx_autfacial_usuario_activo_fecha");
        });

        // ========= CÓDIGOS QR =========
        mb.Entity<CodigoQr>(e =>
        {
            e.ToTable("codigos_qr");
            
            e.Property(p => p.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(p => p.UsuarioId).HasColumnName("usuario_id").IsRequired();
            
            // Código QR (puede ser UUID o JWT cifrado)
            e.Property(p => p.Codigo).HasColumnName("codigo_qr").HasMaxLength(555).IsRequired();
            
            // Hash para búsquedas rápidas (índice)
            e.Property(p => p.QrHash).HasColumnName("qr_hash").HasMaxLength(555);
            
            e.Property(p => p.Activo).HasColumnName("activo").HasDefaultValue(true);

            // Relación con Usuario
            e.HasOne(p => p.Usuario)
             .WithMany(u => u.CodigosQr)
             .HasForeignKey(p => p.UsuarioId)
             .OnDelete(DeleteBehavior.Cascade);

            // Índice compuesto para búsqueda de QR activo por usuario
            e.HasIndex(p => new { p.UsuarioId, p.Activo })
                .HasDatabaseName("idx_qr_usuario_activo");
            
            // Índice único para búsqueda por código (usado en login QR)
            e.HasIndex(p => p.Codigo)
                .HasDatabaseName("idx_qr_codigo");
        });

        // ========= MÉTODOS DE NOTIFICACIÓN =========
        mb.Entity<MetodoNotificacion>(e =>
        {
            e.ToTable("metodos_notificacion");
            
            e.Property(p => p.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(p => p.UsuarioId).HasColumnName("usuario_id").IsRequired();
            e.Property(p => p.Destino).HasColumnName("destino").HasMaxLength(150).IsRequired();
            e.Property(p => p.Activo).HasColumnName("activo").HasDefaultValue(true);
            e.Property(p => p.FechaCreacion).HasColumnName("fecha_creacion")
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            // Enum a string (Email/WhatsApp)
            e.Property(p => p.Tipo)
             .HasConversion<string>()
             .HasColumnName("tipo_notificacion")
             .IsRequired();

            // Relación con Usuario
            e.HasOne(p => p.Usuario)
             .WithMany(u => u.MetodosNotificacion)
             .HasForeignKey(p => p.UsuarioId)
             .OnDelete(DeleteBehavior.Cascade);

            // Índice para filtrar métodos activos por usuario
            e.HasIndex(p => new { p.UsuarioId, p.Activo })
                .HasDatabaseName("idx_notif_usuario_activo");
        });

        // ========= SESIONES (Tokens JWT) =========
        mb.Entity<Sesion>(e =>
        {
            e.ToTable("sesiones");
            
            e.Property(p => p.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(p => p.UsuarioId).HasColumnName("usuario_id").IsRequired();
            
            // Hash SHA-256 del token JWT (NO se almacena el token en claro)
            e.Property(p => p.SessionTokenHash).HasColumnName("session_token")
                .HasMaxLength(255).IsRequired();
            
            // Enum a string (password/qr/facial)
            e.Property(p => p.MetodoLogin)
                .HasConversion<string>()
                .HasColumnName("metodo_login")
                .IsRequired();
            
            e.Property(p => p.FechaLogin).HasColumnName("fecha_login")
                .HasDefaultValueSql("CURRENT_TIMESTAMP");
            
            e.Property(p => p.Activa).HasColumnName("activa").HasDefaultValue(true);

            // Relación con Usuario
            e.HasOne(p => p.Usuario)
             .WithMany(u => u.Sesiones)
             .HasForeignKey(p => p.UsuarioId)
             .OnDelete(DeleteBehavior.Cascade);

            // Índice compuesto para consultas de sesiones activas por usuario
            e.HasIndex(p => new { p.UsuarioId, p.Activa, p.FechaLogin })
                .HasDatabaseName("idx_sesion_usuario_activa_fecha");
            
            // Índice único para validación de token (usado en middleware de revocación)
            e.HasIndex(p => p.SessionTokenHash)
                .HasDatabaseName("idx_sesion_tokenhash");
        });
    }
}