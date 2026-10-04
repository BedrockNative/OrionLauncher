ARG BASE
FROM ${BASE}
ARG FAMILY
ARG HARNESS_SHA256
LABEL org.orion.compat.harness=$HARNESS_SHA256
COPY prepare.sh probe.py /opt/orion-tests/
RUN bash /opt/orion-tests/prepare.sh "$FAMILY"
ENV HOME=/home/orion
USER 1000:1000
WORKDIR /home/orion
ENTRYPOINT ["dbus-run-session", "--", "python3", "/opt/orion-tests/probe.py"]
