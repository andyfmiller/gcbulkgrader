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
            return View(new ErrorViewModel {RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier});
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

        [HttpGet]
        public async Task<IActionResult> BulkGrade(CancellationToken cancellationToken, CourseModel model)
        {
            var appFlow = new AppFlowMetadata(ClientId, ClientSecret);
            var token = await appFlow.Flow.LoadTokenAsync(model.UserId, cancellationToken);
            var credential = new UserCredential(appFlow.Flow, model.UserId, token);

            var gradingModel = new GradingModel
            {
                PersonName = model.PersonName,
                UserId = model.UserId
            };

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

                    gradingModel.CourseName = response.Name;
                }
            }
            catch (Exception e)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, e);
            }

            // Get the list of assignments
            try
            {
                using (var classroomService = new ClassroomService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "gc2lti"
                }))
                {
                    var request = classroomService.Courses.CourseWork.List(model.CourseId);
                    var response = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);

                    gradingModel.Assignments = new List<AssignmentModel>();
                    foreach (var courseWork in response.CourseWork)
                    {
                        if (courseWork.AssociatedWithDeveloper.HasValue
                            && courseWork.AssociatedWithDeveloper.Value
                            && courseWork.MaxPoints.HasValue
                            && courseWork.MaxPoints.Value > 0)
                        {
                            gradingModel.Assignments.Add(new AssignmentModel
                            {
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

            // Get the list of students
            try
            {
                using (var classroomService = new ClassroomService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "gc2lti"
                }))
                {
                    var request = classroomService.Courses.Students.List(model.CourseId);
                    var response = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);

                    gradingModel.Students = new List<StudentModel>();
                    foreach (var student in response.Students)
                    {
                        gradingModel.Students.Add(new StudentModel
                        {
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

            // Fill in the grades

            gradingModel.AssignmentGrades = new AssignmentGrades[gradingModel.Assignments.Count];

            try
            {
                using (var classroomService = new ClassroomService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "gc2lti"
                }))
                {
                    foreach (var assignment in gradingModel.Assignments)
                    {
                        var request = classroomService.Courses.CourseWork.StudentSubmissions.List
                        (
                            model.CourseId,
                            assignment.CourseWorkId
                        );
                        var response = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);

                        var assignmentIndex = gradingModel.Assignments.IndexOf(assignment);
                        gradingModel.AssignmentGrades[assignmentIndex] =
                            new AssignmentGrades
                            {
                                Grades = new double?[gradingModel.Students.Count]
                            };

                        foreach (var submission in response.StudentSubmissions)
                        {
                            var student = gradingModel.Students.SingleOrDefault(s => s.StudentId == submission.UserId);
                            if (student == null) continue;

                            var studentIndex = gradingModel.Students.IndexOf(student);
                            gradingModel.AssignmentGrades[assignmentIndex].Grades[studentIndex] = submission.AssignedGrade;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, e);
            }

            return View(gradingModel);
        }

        [HttpPost]
        public async Task<IActionResult> BulkGrade(CancellationToken cancellationToken, GradingModel model)
        {
            var appFlow = new AppFlowMetadata(ClientId, ClientSecret);
            var token = await appFlow.Flow.LoadTokenAsync(model.UserId, cancellationToken);
            var credential = new UserCredential(appFlow.Flow, model.UserId, token);

            using (var classroomService = new ClassroomService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "gc2lti"
            }))
            {
                for (var assignmentIndex = 0; assignmentIndex < model.AssignmentGrades.Length; assignmentIndex++)
                {
                    var assignmentGrades = model.AssignmentGrades[assignmentIndex];
                    var assignment = model.Assignments[assignmentIndex];

                    // Get the coursework
                    var courseWorkRequest = classroomService.Courses.CourseWork.Get
                    (
                        model.CourseId,
                        assignment.CourseWorkId
                    );

                    //var courseWork = await courseWorkRequest.ExecuteAsync(cancellationToken)
                    //    .ConfigureAwait(false);

                    // Get the students' submissions
                    var submissionsRequest = classroomService.Courses.CourseWork.StudentSubmissions.List
                    (
                        model.CourseId,
                        assignment.CourseWorkId
                    );
                    var submissionsResponse = await submissionsRequest.ExecuteAsync(cancellationToken)
                        .ConfigureAwait(false);

                    for (var studentIndex = 0; studentIndex < assignmentGrades.Grades.Length; studentIndex++)
                    {
                        var grade = assignmentGrades.Grades[studentIndex];
                        var student = model.Students[studentIndex];

                        var submission = submissionsResponse.StudentSubmissions
                            .SingleOrDefault(s => s.UserId == student.StudentId);

                        if (submission != null)
                        {
                            submission.AssignedGrade = grade;
                            submission.DraftGrade = grade;

                            var patchRequest = classroomService.Courses.CourseWork.StudentSubmissions.Patch
                            (
                                submission,
                                submission.CourseId,
                                submission.CourseWorkId,
                                submission.Id
                            );
                            patchRequest.UpdateMask = "AssignedGrade,DraftGrade";
                            await patchRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }

            var courseModel = new CourseModel
            {
                CourseId = model.CourseId,
                PersonName = model.PersonName,
                UserId = model.UserId
            };

            return RedirectToAction(nameof(BulkGrade), courseModel);
        }
    }
}
