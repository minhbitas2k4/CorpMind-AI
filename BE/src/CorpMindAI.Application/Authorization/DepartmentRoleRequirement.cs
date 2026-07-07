using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
namespace CorpMindAI.Application.Authorization
{
    public class DepartmentRoleRequirement : IAuthorizationRequirement
    {
        public string[] AllowedRoles { get; }

        public DepartmentRoleRequirement(params string[] allowedRoles)
        {
            AllowedRoles = allowedRoles;
        }
    }
}
