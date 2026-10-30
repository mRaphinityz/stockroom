using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Stockroom.Web.Domain;

namespace Stockroom.Web.Data;

public sealed class StockroomDbContext(DbContextOptions<StockroomDbContext> options) : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<SalesOrder> Orders => Set<SalesOrder>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<StockMovement> Movements => Set<StockMovement>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<Product>(entity =>
        {
            entity.HasIndex(product => product.Sku).IsUnique();
            entity.Property(product => product.Sku).HasMaxLength(32);
            entity.Property(product => product.Name).HasMaxLength(100);
            entity.Property(product => product.Version).IsConcurrencyToken();
            entity.Ignore(product => product.Available);
            entity.ToTable("Products", table =>
            {
                table.HasCheckConstraint("CK_Product_Stock", "\"OnHand\" >= 0 AND \"OnHand\" <= 1000000 AND \"Reserved\" >= 0 AND \"Reserved\" <= \"OnHand\"");
                table.HasCheckConstraint("CK_Product_Price", "\"PriceCents\" >= 0 AND \"PriceCents\" <= 100000000");
                table.HasCheckConstraint("CK_Product_Reorder", "\"ReorderPoint\" >= 0 AND \"ReorderPoint\" <= 1000000");
            });
        });
        builder.Entity<SalesOrder>(entity =>
        {
            entity.HasIndex(order => order.IdempotencyKey).IsUnique();
            entity.HasIndex(order => order.CreatedAt);
            entity.Property(order => order.Customer).HasMaxLength(100);
            entity.Property(order => order.IdempotencyKey).HasMaxLength(100);
            entity.Property(order => order.Status).HasConversion<string>();
            entity.Ignore(order => order.TotalCents);
            entity.HasMany(order => order.Lines).WithOne().HasForeignKey(line => line.SalesOrderId);
        });
        builder.Entity<OrderLine>(entity =>
        {
            entity.HasOne(line => line.Product).WithMany().HasForeignKey(line => line.ProductId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(line => new { line.SalesOrderId, line.ProductId }).IsUnique();
            entity.ToTable("OrderLines", table => table.HasCheckConstraint("CK_OrderLine_Quantity", "\"Quantity\" > 0 AND \"Quantity\" <= 10000"));
        });
        builder.Entity<StockMovement>().HasOne(movement => movement.Product).WithMany()
            .HasForeignKey(movement => movement.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<StockMovement>().HasIndex(movement => movement.CreatedAt);
    }
}
