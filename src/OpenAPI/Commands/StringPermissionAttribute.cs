﻿using System;
using System.Collections.Generic;
using System.Text;

namespace OpenAPI.Commands
{
    // TODO: Figure a better name then StringPermission
    public class StringPermissionAttribute : CommandPermissionAttribute
    {
        public string Permission { get; set; }

        public StringPermissionAttribute(string permission)
        {
            Permission = permission;
        }
    }
}
