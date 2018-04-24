using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using gcbulkgrader.Models;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.AspMvcCore;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Classroom.v1;
using Google.Apis.Classroom.v1.Data;
using Google.Apis.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Configuration;

namespace gcbulkgrader.Controllers
{
    public class HomeController : Controller
    {
        private readonly IConfiguration _configuration;

        public HomeController(IConfiguration configuration)
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

        public IActionResult Index()
        {
            return View();
        }

        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }

        public async Task<IActionResult> SelectCourse(CancellationToken cancellationToken, CourseSelectionModel model)
        {
            var result = await new AuthorizationCodeMvcApp(this, new AppFlowMetadata(ClientId, ClientSecret))
                .AuthorizeAsync(cancellationToken)
                .ConfigureAwait(false);

            if (result.Credential == null)
            {
                return Redirect(result.RedirectUri);
            }
            model.UserId = result.Credential.UserId;

            // Get the list of courses
            try
            {
                using (var classroomService = new ClassroomService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = result.Credential,
                    ApplicationName = "gc2lti"
                }))
                {
                    // Get the user's name
                    var profileRequest = classroomService.UserProfiles.Get("me");
                    var profile = await profileRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                    model.PersonName = profile.Name.FullName;

                    // Get the list of the user's courses
                    var coursesRequest = classroomService.Courses.List();
                    coursesRequest.CourseStates = CoursesResource.ListRequest.CourseStatesEnum.ACTIVE;
                    coursesRequest.TeacherId = "me";
                    ListCoursesResponse coursesResponse = null;
                    var courses = new List<SelectListItem>();
                    do
                    {
                        if (coursesResponse != null)
                        {
                            coursesRequest.PageToken = coursesResponse.NextPageToken;
                        }

                        coursesResponse =
                            await coursesRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                        if (coursesResponse.Courses != null)
                        {
                            courses.AddRange
                            (
                                coursesResponse.Courses.Select(c => new SelectListItem
                                {
                                    Value = c.Id,
                                    Text = c.Name
                                })
                            );
                        }
                    } while (!string.IsNullOrEmpty(coursesResponse.NextPageToken));

                    model.Courses = new SelectList(courses, "Value", "Text");

                    return View(model);
                }
            }
            catch (GoogleApiException e) when (e.Message.Contains("invalid authentication credentials"))
            {
                // Force a new UserId
                TempData.Remove("user");
                return RedirectToAction("SelectCourse", model);
            }
            catch (TokenResponseException e) when (e.Message.Contains("invalid_grant"))
            {
                // Force a new UserId
                TempData.Remove("user");
                return RedirectToAction("SelectCourse", model);
            }
            catch (Exception e)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, e);
            }
        }

        public async Task<IActionResult> BulkGrade(CancellationToken cancellationToken, GradingModel model)
        {
            var appFlow = new AppFlowMetadata(ClientId, ClientSecret);
            var token = await appFlow.Flow.LoadTokenAsync(model.UserId, cancellationToken);
            var credential = new UserCredential(appFlow.Flow, model.UserId, token);

            try
            {
                // Get the course name
                using (var classroomService = new ClassroomService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "gc2lti"
                }))
                {
                    var request = classroomService.Courses.Get(model.CourseId);
                    var response = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);

                    model.CourseName = response.Name;
                }
            }
            catch (Exception e)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, e);
            }

            try
            {
                // Get a list of assignments
                using (var classroomService = new ClassroomService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "gc2lti"
                }))
                {
                    var request = classroomService.Courses.CourseWork.List(model.CourseId);
                    var response = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);

                    model.Assignments = new List<AssignmentModel>();
                    foreach (var courseWork in response.CourseWork)
                    {
                        if (courseWork.AssociatedWithDeveloper.HasValue 
                            && courseWork.AssociatedWithDeveloper.Value
                            && courseWork.MaxPoints.HasValue
                            && courseWork.MaxPoints.Value > 0)
                        {
                            model.Assignments.Add(new AssignmentModel
                            {
                                CourseId = model.CourseId,
                                CourseName = model.CourseName,
                                CourseWorkId = courseWork.Id,
                                CourseWorkName = courseWork.Title,
                                MaxPoints = courseWork.MaxPoints.Value
                            });
                        }
                    }
                }
            }
            catch (Exception e)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, e);
            }

            try
            {
                // Get a list of students
                using (var classroomService = new ClassroomService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "gc2lti"
                }))
                {
                    var request = classroomService.Courses.Students.List(model.CourseId);
                    var response = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);

                    model.Students = new List<StudentModel>();
                    foreach (var student in response.Students)
                    {
                        model.Students.Add(new StudentModel
                        {
                            CourseId = model.CourseId,
                            CourseName = model.CourseName,
                            StudentId = student.UserId,
                            StudentName = student.Profile.Name.FullName
                        });
                    }
                }
            }
            catch (Exception e)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, e);
            }

            try
            {
                // Get the student submissions
                using (var classroomService = new ClassroomService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "gc2lti"
                }))
                {
                    foreach (var assignment in model.Assignments)
                    {
                        var request =
                            classroomService.Courses.CourseWork.StudentSubmissions.List(model.CourseId,
                                assignment.CourseWorkId);
                        var response = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);

                        model.Grades = new List<AssignmentGrade>();
                        foreach (var submission in response.StudentSubmissions)
                        {
                            model.Grades.Add(new AssignmentGrade
                            {
                                CourseId = submission.CourseId,
                                CourseWorkId = submission.CourseWorkId,
                                SubmissionId = submission.Id,
                                StudentId = submission.UserId,
                                AssignedGrade = submission.AssignedGrade
                            });
                        }
                    }
                }
            }
            catch (Exception e)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, e);
            }

            return View(model);
        }
    }
}
