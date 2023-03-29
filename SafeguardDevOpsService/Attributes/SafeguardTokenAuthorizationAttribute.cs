﻿using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using OneIdentity.DevOps.Authorization;
using OneIdentity.DevOps.Logic;
#pragma warning disable 1591

namespace OneIdentity.DevOps.Attributes
{
    public class SafeguardTokenAuthorizationAttribute : SafeguardAuthorizationBaseAttribute, IAuthorizationFilter
    {
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            var sppToken = AttributeHelper.GetSppToken(context.HttpContext);
            if (sppToken == null)
            {
                context.Result = new DevOpsUnauthorizedResult("Authorization Failed: Invalid or missing SPP token");
                return;
            }

            var service = (ISafeguardLogic)context.HttpContext.RequestServices.GetService(typeof(ISafeguardLogic));
            if (!service.ValidateLogin(sppToken))
            {
                context.Result = new DevOpsUnauthorizedResult("Authorization Failed: Invalid token");
                return;
            }

            service.SetThreadData(sppToken);
            var managementConnection = AuthorizedCache.Instance.FindByToken(sppToken);
            context.HttpContext.Response.Cookies.Append(WellKnownData.SessionKeyCookieName, managementConnection.SessionKey, new CookieOptions(){Secure = true});
        }
    }
}
