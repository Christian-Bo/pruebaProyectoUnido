using Microsoft.EntityFrameworkCore;
using Auth.Domain.Entities;

namespace Auth.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> opts) : base(opts) { } //Constructor estandar

    //Conexión con cada una de las tablas de la base de datos por nombre en caso de no existir las crea
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<AutenticacionFacial> AutenticacionFacial => Set<AutenticacionFacial>();
    public DbSet<CodigoQr> CodigosQr => Set<CodigoQr>();
    public DbSet<MetodoNotificacion> MetodosNotificacion => Set<MetodoNotificacion>();
    public DbSet<Sesion> Sesiones => Set<Sesion>();

    /*Este es un modelo que sea crea para que todos los campos que se encuentran en las tablas sean reconocidos en los .cs
    de la carpeta Domain esto sirve para que no sea tan necesario colocar el mismo nombre de las tablas en cada uno de los .cs
    */
    protected override void OnModelCreating(ModelBuilder mb)
    {
        // Charset/collation (Pomelo)
        mb.HasCharSet("utf8mb4").UseCollation("utf8mb4_0900_ai_ci");

        // usuarios
        mb.Entity<Usuario>(e =>
        {
            e.ToTable("usuarios");
            e.Property(p => p.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(p => p.UsuarioNombre).HasColumnName("usuario");
            e.Property(p => p.Email).HasColumnName("email");
            e.Property(p => p.NombreCompleto).HasColumnName("nombre_completo");
            e.Property(p => p.PasswordHash).HasColumnName("password_hash");
            e.Property(p => p.Telefono).HasColumnName("telefono");
            e.Property(p => p.FechaCreacion).HasColumnName("fecha_creacion")
                .HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(p => p.Activo).HasColumnName("activo").HasDefaultValue(true);
        });

        // autenticacion_facial
        mb.Entity<AutenticacionFacial>(e =>
        {
            e.ToTable("autenticacion_facial");
            e.Property(p => p.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(p => p.UsuarioId).HasColumnName("usuario_id");
            e.Property(p => p.EncodingFacial).HasColumnName("encoding_facial")
                .HasColumnType("TEXT");
            e.Property(p => p.ImagenReferencia).HasColumnName("imagen_referencia")
                .HasColumnType("TEXT");
            e.Property(p => p.Activo).HasColumnName("activo").HasDefaultValue(true);
            e.Property(p => p.FechaCreacion).HasColumnName("fecha_creacion")
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            e.HasOne(p => p.Usuario)
             .WithMany(u => u.AutenticacionesFaciales)
             .HasForeignKey(p => p.UsuarioId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // codigos_qr
        mb.Entity<CodigoQr>(e =>
        {
            e.ToTable("codigos_qr");
            e.Property(p => p.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(p => p.UsuarioId).HasColumnName("usuario_id");
            e.Property(p => p.Codigo).HasColumnName("codigo_qr").HasMaxLength(555);
            e.Property(p => p.QrHash).HasColumnName("qr_hash").HasMaxLength(555);
            e.Property(p => p.Activo).HasColumnName("activo").HasDefaultValue(true);

            e.HasOne(p => p.Usuario)
             .WithMany(u => u.CodigosQr)
             .HasForeignKey(p => p.UsuarioId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // metodos_notificacion
        mb.Entity<MetodoNotificacion>(e =>
        {
            e.ToTable("metodos_notificacion");
            e.Property(p => p.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(p => p.UsuarioId).HasColumnName("usuario_id");
            e.Property(p => p.Destino).HasColumnName("destino").HasMaxLength(150);
            e.Property(p => p.Activo).HasColumnName("activo").HasDefaultValue(true);
            e.Property(p => p.FechaCreacion).HasColumnName("fecha_creacion")
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            // enum -> string (para que sea compatible con ENUM('email','whatsapp'))
            e.Property(p => p.Tipo)
             .HasConversion<string>()
             .HasColumnName("tipo_notificacion");

            e.HasOne(p => p.Usuario)
             .WithMany(u => u.MetodosNotificacion)
             .HasForeignKey(p => p.UsuarioId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // sesiones
        mb.Entity<Sesion>(e =>
        {
            e.ToTable("sesiones");
            e.Property(p => p.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(p => p.UsuarioId).HasColumnName("usuario_id");
            e.Property(p => p.SessionTokenHash).HasColumnName("session_token").HasMaxLength(255);
            e.Property(p => p.MetodoLogin).HasConversion<string>().HasColumnName("metodo_login");
            e.Property(p => p.FechaLogin).HasColumnName("fecha_login")
                .HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(p => p.Activa).HasColumnName("activa").HasDefaultValue(true);

            e.HasOne(p => p.Usuario)
             .WithMany(u => u.Sesiones)
             .HasForeignKey(p => p.UsuarioId)
             .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
