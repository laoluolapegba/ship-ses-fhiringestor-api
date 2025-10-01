using Microsoft.EntityFrameworkCore;
using Ship.Ses.Ingestor.Domain;

namespace Ship.Ses.Ingestor.Infrastructure.Repositories
{
    public class AppDbContext : DbContext, IAppDbContext
    {

        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }

        //public DbSet<Order> Orders { get; set; }
        //public DbSet<Customer> Customers { get; set; }
       

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
            base.OnModelCreating(modelBuilder);
        }
    }

    public interface IAppDbContext
    {
        //public DbSet<Order> Orders { get; set; }
        //public DbSet<Customer> Customers { get; set; }
        
    }
}
