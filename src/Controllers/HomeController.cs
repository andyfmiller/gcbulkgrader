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

        [HttpGet]
        public async Task<IActionResult> SelectCourse(CancellationToken cancellationToken)
        {
            var result = await new AuthorizationCodeMvcApp(this, new AppFlowMetadata(ClientId, ClientSecret))
                .AuthorizeAsync(cancellationToken)
                .ConfigureAwait(false);

            if (result.Credential == null)
            {
                return Redirect(result.RedirectUri);
            }

            var model = new CourseSelectionModel
            {
                UserId = result.Credential.UserId
            };

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
                    model.PersonImage = profile.PhotoUrl;
                    model.PersonName = profile.Name.FullName;

                    // Get the list of the user's courses
                    model.Courses = new List<CourseModel>();

                    var coursesRequest = classroomService.Courses.List();
                    coursesRequest.CourseStates = CoursesResource.ListRequest.CourseStatesEnum.ACTIVE;
                    coursesRequest.TeacherId = "me";

                    ListCoursesResponse coursesResponse = null;
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
                            foreach (var course in coursesResponse.Courses)
                            {
                                model.Courses.Add(new CourseModel
                                {
                                    CourseId = course.Id,
                                    CourseName = course.Name
                                });
                            }
                        }
                    } while (!string.IsNullOrEmpty(coursesResponse.NextPageToken));

                    return View(model);
                }
            }
            catch (GoogleApiException e) when (e.Message.Contains("invalid authentication credentials"))
            {
                // Force a new UserId
                TempData.Remove("user");
                return RedirectToAction("SelectCourse");
            }
            catch (TokenResponseException e) when (e.Message.Contains("invalid_grant"))
            {
                // Force a new UserId
                TempData.Remove("user");
                return RedirectToAction("SelectCourse");
            }
            catch (Exception e)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, e);
            }
        }

        [HttpPost]
        public IActionResult SelectCourse(CancellationToken cancellationToken, CourseSelectionModel model)
        {
            var courseModel = model.Courses.SingleOrDefault(c => c.CourseId == model.CourseId);
            if (courseModel == null)
            {
                return RedirectToAction("SelectCourse");
            }

            courseModel.PersonImage = model.PersonImage;
            courseModel.PersonName = model.PersonName;
            courseModel.UserId = model.UserId;

            return RedirectToAction(nameof(BulkGrade), courseModel);
        }

        [HttpGet]
        public async Task<IActionResult> BulkGrade(CancellationToken cancellationToken, CourseModel model)
        {
            var appFlow = new AppFlowMetadata(ClientId, ClientSecret);
            var token = await appFlow.Flow.LoadTokenAsync(model.UserId, cancellationToken);
            var credential = new UserCredential(appFlow.Flow, model.UserId, token);

            var gradingModel = new GradingModel
            {
                PersonImage = model.PersonImage,
                PersonName = model.PersonName,
                UserId = model.UserId,
                CourseId = model.CourseId,
                CourseName = model.CourseName
            };

            try
            {
                using (var classroomService = new ClassroomService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "gc2lti"
                }))
                {
                    gradingModel.Assignments = await GetAssignments(model, classroomService, cancellationToken);
                    gradingModel.Students = await GetStudents(model, classroomService, cancellationToken);
                    gradingModel.AssignmentGrades = await GetGrades(model, gradingModel.Assignments, gradingModel.Students, classroomService, cancellationToken);
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

            try
            {
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
            }
            catch (Exception e)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, e);
            }

            var courseModel = new CourseModel
            {
                CourseId = model.CourseId,
                CourseName = model.CourseName,
                PersonName = model.PersonName,
                UserId = model.UserId
            };

            return RedirectToAction(nameof(BulkGrade), courseModel);
        }

        private static async Task<IList<AssignmentModel>> GetAssignments(CourseModel model, ClassroomService classroomService, CancellationToken cancellationToken)
        {
            var request = classroomService.Courses.CourseWork.List(model.CourseId);
            var response = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);

            var assignments = new List<AssignmentModel>();
            foreach (var courseWork in response.CourseWork)
            {
                if (courseWork.AssociatedWithDeveloper.HasValue
                    && courseWork.AssociatedWithDeveloper.Value
                    && courseWork.MaxPoints.HasValue
                    && courseWork.MaxPoints.Value > 0)
                {
                    assignments.Add(new AssignmentModel
                    {
                        CourseWorkId = courseWork.Id,
                        CourseWorkName = courseWork.Title,
                        MaxPoints = courseWork.MaxPoints.Value
                    });
                }
            }

            return assignments;
        }

        private static async Task<IList<StudentModel>> GetStudents(CourseModel model, ClassroomService classroomService, CancellationToken cancellationToken)
        {
            var request = classroomService.Courses.Students.List(model.CourseId);
            var response = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);

            var students = new List<StudentModel>();
            foreach (var student in response.Students)
            {
                students.Add(new StudentModel
                {
                    StudentId = student.UserId,
                    StudentName = student.Profile.Name.FullName
                });
            }

            return students;
        }

        private async Task<AssignmentGrades[]> GetGrades(CourseModel model, IList<AssignmentModel> assignments, IList<StudentModel> students, ClassroomService classroomService, CancellationToken cancellationToken)
        {
            var assignmentGrades = new AssignmentGrades[assignments.Count];
            foreach (var assignment in assignments)
            {
                // Get the student submissions for this assignment
                var request = classroomService.Courses.CourseWork.StudentSubmissions.List
                (
                    model.CourseId,
                    assignment.CourseWorkId
                );
                var response = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);

                var assignmentIndex = assignments.IndexOf(assignment);
                assignmentGrades[assignmentIndex] =
                    new AssignmentGrades
                    {
                        Grades = new double?[students.Count]
                    };

                foreach (var submission in response.StudentSubmissions)
                {
                    var student = students.SingleOrDefault(s => s.StudentId == submission.UserId);
                    if (student == null) continue;

                    var studentIndex = students.IndexOf(student);
                    assignmentGrades[assignmentIndex].Grades[studentIndex] = submission.AssignedGrade;
                }
            }

            return assignmentGrades;
        }
    }
}
