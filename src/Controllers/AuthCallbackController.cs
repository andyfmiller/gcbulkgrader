using Google.Apis.Auth.OAuth2.AspMvcCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace gcbulkgrader.Controllers
{
    [Route("[controller]")]
    public class AuthCallbackController : Google.Apis.Auth.OAuth2.AspMvcCore.Controllers.AuthCallbackController
    {
        public IConfiguration Configuration { get; set; }

        public AuthCallbackController(IConfiguration config)
        {
            Configuration = config;
        }

        protected override FlowMetadata FlowData
        {
            get
            {
                var clientId = Configuration["Authentication:Google:ClientId"];
                var clientSecret = Configuration["Authentication:Google:ClientSecret"];

                return new AppFlowMetadata(clientId, clientSecret);
            }
        }
    }
}