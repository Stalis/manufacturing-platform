import "@typespec/http";
import "./decorators.tsp";

using Http;

namespace Manufacturing.Http;

/** A UUID carried as a JSON string; maps to System.Guid. */
@format("uuid")
scalar Guid extends string;

/** RFC 9457 problem details; the server uses Microsoft.AspNetCore.Mvc.ProblemDetails. */
model ProblemDetails {
  type?: string;
  title?: string;
  status?: int32;
  detail?: string;
  instance?: string;
}

/** An error response carrying RFC 9457 problem details. */
@error
model Problem<Status extends int32> {
  @statusCode statusCode: Status;
  @header("content-type") contentType: "application/problem+json";
  @body problem: ProblemDetails;
}

alias BadRequest = Problem<400>;
alias NotFound = Problem<404>;
alias Conflict = Problem<409>;
