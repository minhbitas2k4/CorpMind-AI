using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CorpMindAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using BCrypt.Net;

namespace CorpMindAI.Infrastructure.Services
{
    public class PasswordMigrationService
    {
        private readonly CorpMindDbContext _context;

        public PasswordMigrationService(CorpMindDbContext context)
        {
            _context = context;
        }

        public async Task MigratePasswordsAsync()
        {
            var users = await _context.Users.ToListAsync();

            int migratedCount = 0;

            foreach (var user in users)
            {
                if (string.IsNullOrWhiteSpace(user.PasswordHash))
                    continue;

                if (user.PasswordHash.StartsWith("$2"))
                    continue;

                user.PasswordHash =
                    BCrypt.Net.BCrypt.HashPassword(user.PasswordHash);

                migratedCount++;
            }

            await _context.SaveChangesAsync();

            Console.WriteLine($"Migrated {migratedCount} passwords.");
        }
    }
}
