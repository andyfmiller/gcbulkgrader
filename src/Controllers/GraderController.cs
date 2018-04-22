using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using gcbulkgrader.Models;
using Google.Apis.Auth.OAuth2.AspMvcCore;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace gcbulkgrader.Controllers
{
    public class GraderController : Controller
    {
        private readonly IConfiguration _configuration;

        public GraderController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        private string ClientId
        {
            get { return _configuration["Authentication:Google:ClientId"]; }
        }

        private string ClientSecret
        {
            get { return _configuration["Authentication:Google:ClientSecret"]; }
        }

        public async Task<IActionResult> Index(CancellationToken cancellationToken, CourseSelectionModel model)
        {
            var result = await new AuthorizationCodeMvcApp(this, new AppFlowMetadata(ClientId, ClientSecret))
                .AuthorizeAsync(cancellationToken)
                .ConfigureAwait(false);

            if (result.Credential == null)
            {
                return Redirect(result.RedirectUri);
            }
            model.UserId = result.Credential.UserId;

            return View();
        }
    }
}